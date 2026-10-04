// FmCore.cpp - MP-Commander 네이티브 파일시스템 모듈
#include "FmCore.h"

#include <windows.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <winnetwk.h>

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "mpr.lib")
#pragma comment(lib, "shell32.lib")

namespace {

constexpr int kBatchSize = 1024;

// MAX_PATH 를 넘는 경로는 \\?\ 접두어를 붙인다. (입력은 정규화된 절대 경로라고 가정)
std::wstring ExtendedPath(const std::wstring& p)
{
    if (p.size() < MAX_PATH - 12 || p.rfind(L"\\\\?\\", 0) == 0)
        return p;
    if (p.rfind(L"\\\\", 0) == 0)
        return L"\\\\?\\UNC\\" + p.substr(2);
    return L"\\\\?\\" + p;
}

std::wstring JoinPath(const std::wstring& dir, const wchar_t* name)
{
    std::wstring r = dir;
    if (!r.empty() && r.back() != L'\\')
        r += L'\\';
    r += name;
    return r;
}

inline uint64_t ToU64(DWORD hi, DWORD lo) { return (uint64_t(hi) << 32) | lo; }

inline bool IsDots(const wchar_t* n)
{
    return n[0] == L'.' && (n[1] == 0 || (n[1] == L'.' && n[2] == 0));
}

inline FmEntry MakeEntry(const WIN32_FIND_DATAW& fd)
{
    FmEntry e{};
    e.size = ToU64(fd.nFileSizeHigh, fd.nFileSizeLow);
    e.lastWrite = ToU64(fd.ftLastWriteTime.dwHighDateTime, fd.ftLastWriteTime.dwLowDateTime);
    e.attributes = fd.dwFileAttributes;
    return e;
}

HANDLE FindFirst(const std::wstring& dir, WIN32_FIND_DATAW& fd)
{
    std::wstring pattern = ExtendedPath(JoinPath(dir, L"*"));
    return FindFirstFileExW(pattern.c_str(), FindExInfoBasic, &fd, FindExSearchNameMatch, nullptr,
                            FIND_FIRST_EX_LARGE_FETCH);
}

struct ProgressCtx {
    FmProgressCallback cb;
    void* user;
    volatile long* cancel;
};

DWORD CALLBACK CopyProgressRoutine(LARGE_INTEGER totalSize, LARGE_INTEGER transferred, LARGE_INTEGER, LARGE_INTEGER,
                                   DWORD, DWORD, HANDLE, HANDLE, LPVOID data)
{
    auto* ctx = static_cast<ProgressCtx*>(data);
    if (ctx->cancel && *ctx->cancel)
        return PROGRESS_CANCEL;
    if (ctx->cb && ctx->cb(uint64_t(transferred.QuadPart), uint64_t(totalSize.QuadPart), ctx->user) != 0)
        return PROGRESS_CANCEL;
    return PROGRESS_CONTINUE;
}

void ClearReadOnly(const std::wstring& path)
{
    DWORD a = GetFileAttributesW(path.c_str());
    if (a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_READONLY))
        SetFileAttributesW(path.c_str(), a & ~FILE_ATTRIBUTE_READONLY);
}

} // namespace

// ───────────────────────── 공통 ─────────────────────────

FM_API int FM_CALL FmGetVersion(void) { return 1; }

FM_API void* FM_CALL FmOpenCurrentThread(void)
{
    HANDLE h = nullptr;
    if (!DuplicateHandle(GetCurrentProcess(), GetCurrentThread(), GetCurrentProcess(), &h, 0, FALSE,
                         DUPLICATE_SAME_ACCESS))
        return nullptr;
    return h;
}

FM_API int FM_CALL FmCancelThreadIo(void* thread)
{
    return thread && CancelSynchronousIo(static_cast<HANDLE>(thread)) ? 1 : 0;
}

FM_API void FM_CALL FmCloseHandle(void* handle)
{
    if (handle)
        CloseHandle(static_cast<HANDLE>(handle));
}

FM_API int FM_CALL FmCompareNatural(const wchar_t* a, const wchar_t* b)
{
    return StrCmpLogicalW(a ? a : L"", b ? b : L"");
}

// ───────────────────────── 디렉터리 열거 ─────────────────────────

FM_API int FM_CALL FmEnumDirectory(const wchar_t* path, FmEntryBatchCallback cb, void* user,
                                   volatile long* cancelFlag, volatile long* progress)
{
    if (!path || !cb)
        return ERROR_INVALID_PARAMETER;

    WIN32_FIND_DATAW fd;
    HANDLE h = FindFirst(path, fd);
    if (h == INVALID_HANDLE_VALUE) {
        DWORD e = GetLastError();
        return e == ERROR_FILE_NOT_FOUND ? 0 : int(e);   // 빈 드라이브 루트
    }

    std::vector<std::wstring> names;
    std::vector<FmEntry> entries;
    names.reserve(kBatchSize);
    entries.reserve(kBatchSize);

    auto flush = [&]() -> bool {
        if (entries.empty())
            return true;
        for (size_t i = 0; i < entries.size(); ++i)
            entries[i].name = names[i].c_str();
        int keepGoing = cb(entries.data(), int(entries.size()), user);
        entries.clear();
        names.clear();
        return keepGoing != 0;
    };

    int result = 0;
    BOOL more = TRUE;
    while (more) {
        if (cancelFlag && *cancelFlag) {
            result = ERROR_CANCELLED;
            break;
        }
        if (!IsDots(fd.cFileName)) {
            names.emplace_back(fd.cFileName);
            entries.push_back(MakeEntry(fd));
            if (progress)
                InterlockedIncrement(progress);
            if (int(entries.size()) >= kBatchSize && !flush()) {
                result = ERROR_CANCELLED;
                break;
            }
        }
        more = FindNextFileW(h, &fd);
        if (!more) {
            DWORD e = GetLastError();
            if (e != ERROR_NO_MORE_FILES)
                result = int(e);
        }
    }
    FindClose(h);

    if (result == 0 && !flush())
        result = ERROR_CANCELLED;
    return result;
}

// ───────────────────────── 파일 작업 ─────────────────────────

FM_API int FM_CALL FmCopyFile(const wchar_t* src, const wchar_t* dst, int overwrite,
                              FmProgressCallback cb, void* user, volatile long* cancelFlag)
{
    if (!src || !dst)
        return ERROR_INVALID_PARAMETER;
    std::wstring s = ExtendedPath(src), d = ExtendedPath(dst);
    if (overwrite)
        ClearReadOnly(d);

    ProgressCtx ctx{ cb, user, cancelFlag };
    DWORD flags = overwrite ? 0 : COPY_FILE_FAIL_IF_EXISTS;
    LPBOOL cancel = cancelFlag ? reinterpret_cast<LPBOOL>(const_cast<long*>(cancelFlag)) : nullptr;
    if (!CopyFileExW(s.c_str(), d.c_str(), CopyProgressRoutine, &ctx, cancel, flags))
        return int(GetLastError());
    return 0;
}

FM_API int FM_CALL FmMoveFile(const wchar_t* src, const wchar_t* dst, int overwrite,
                              FmProgressCallback cb, void* user, volatile long* cancelFlag)
{
    if (!src || !dst)
        return ERROR_INVALID_PARAMETER;
    std::wstring s = ExtendedPath(src), d = ExtendedPath(dst);
    if (overwrite)
        ClearReadOnly(d);

    ProgressCtx ctx{ cb, user, cancelFlag };
    DWORD flags = MOVEFILE_COPY_ALLOWED | (overwrite ? MOVEFILE_REPLACE_EXISTING : 0);
    if (!MoveFileWithProgressW(s.c_str(), d.c_str(), CopyProgressRoutine, &ctx, flags))
        return int(GetLastError());
    return 0;
}

FM_API int FM_CALL FmShellDelete(const wchar_t* pathsDoubleNull, int permanent, void* hwnd)
{
    if (!pathsDoubleNull)
        return ERROR_INVALID_PARAMETER;
    SHFILEOPSTRUCTW op{};
    op.hwnd = static_cast<HWND>(hwnd);
    op.wFunc = FO_DELETE;
    op.pFrom = pathsDoubleNull;
    op.fFlags = FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | (permanent ? 0 : FOF_ALLOWUNDO);
    int r = SHFileOperationW(&op);
    if (r == 0 && op.fAnyOperationsAborted)
        return ERROR_CANCELLED;
    return r;
}

// ───────────────────────── 셸 / 볼륨 ─────────────────────────

FM_API void* FM_CALL FmGetShellIcon(const wchar_t* name, int isDirectory, int large)
{
    // SHGFI_USEFILEATTRIBUTES: 디스크에 접근하지 않는다 (네트워크 경로에서도 막히지 않음)
    SHFILEINFOW sfi{};
    UINT flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
    DWORD attr = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
    if (!SHGetFileInfoW(name ? name : L"file", attr, &sfi, sizeof(sfi), flags))
        return nullptr;
    return sfi.hIcon;
}

FM_API int FM_CALL FmGetUncForDrive(const wchar_t* drive, wchar_t* buffer, int bufferLength)
{
    if (!drive || !buffer || bufferLength <= 0)
        return ERROR_INVALID_PARAMETER;
    DWORD len = DWORD(bufferLength);
    return int(WNetGetConnectionW(drive, buffer, &len));
}

FM_API int FM_CALL FmGetDiskFree(const wchar_t* path, uint64_t* freeBytes, uint64_t* totalBytes)
{
    ULARGE_INTEGER avail{}, total{}, totalFree{};
    if (!GetDiskFreeSpaceExW(path, &avail, &total, &totalFree))
        return int(GetLastError());
    if (freeBytes)
        *freeBytes = avail.QuadPart;
    if (totalBytes)
        *totalBytes = total.QuadPart;
    return 0;
}

// ───────────────────────── 파일 찾기 ─────────────────────────

namespace search {

using Searcher = std::boyer_moore_horspool_searcher<std::string::const_iterator>;

struct Worker {
    std::atomic<HANDLE> thread{ nullptr };
    std::atomic<ULONGLONG> ioStart{ 0 };        // 0 = I/O 중 아님
    std::atomic<ULONGLONG> cancelledStart{ 0 };
};

struct State {
    std::wstring root, mask, text;
    bool caseSensitive = false, recursive = true, includeHidden = false;
    int threadCount = 4;
    ULONGLONG ioTimeout = 5000;

    std::vector<std::string> patterns;   // 내용 검색용 바이트 패턴 (UTF-8 / ANSI / UTF-16LE)
    std::vector<Searcher> searchers;
    size_t maxPatternLength = 0;

    FmEntryBatchCallback cb = nullptr;
    void* user = nullptr;
    std::mutex cbMutex;
    bool released = false;

    std::mutex queueMutex;
    std::condition_variable queueCv;
    std::deque<std::wstring> dirs;
    int busy = 0;
    std::atomic<bool> cancel{ false };
    std::atomic<bool> running{ true };

    std::atomic<long long> dirsScanned{ 0 }, filesScanned{ 0 }, found{ 0 }, errors{ 0 };
    std::mutex currentMutex;
    std::wstring currentDir;

    std::vector<std::unique_ptr<Worker>> workers;

    ~State()
    {
        for (auto& w : workers)
            if (HANDLE h = w->thread.load())
                CloseHandle(h);
    }
};

struct IoScope {
    Worker& w;
    explicit IoScope(Worker& worker) : w(worker) { w.ioStart = GetTickCount64(); }
    ~IoScope() { w.ioStart = 0; }
};

void AddPattern(State& s, std::string bytes)
{
    if (bytes.empty())
        return;
    if (!s.caseSensitive)
        for (char& c : bytes)
            if (c >= 'A' && c <= 'Z')
                c = char(c + 32);
    if (std::find(s.patterns.begin(), s.patterns.end(), bytes) == s.patterns.end())
        s.patterns.push_back(std::move(bytes));
}

std::string ToMultiByte(UINT codePage, const std::wstring& w)
{
    int n = WideCharToMultiByte(codePage, 0, w.c_str(), int(w.size()), nullptr, 0, nullptr, nullptr);
    std::string r(size_t(std::max(n, 0)), '\0');
    if (n > 0)
        WideCharToMultiByte(codePage, 0, w.c_str(), int(w.size()), r.data(), n, nullptr, nullptr);
    return r;
}

void BuildPatterns(State& s)
{
    if (s.text.empty())
        return;
    AddPattern(s, ToMultiByte(CP_UTF8, s.text));
    AddPattern(s, ToMultiByte(CP_ACP, s.text));   // 한글 ANSI(CP949) 텍스트 파일 대응
    AddPattern(s, std::string(reinterpret_cast<const char*>(s.text.data()), s.text.size() * sizeof(wchar_t)));
    for (auto& p : s.patterns) {
        s.maxPatternLength = std::max(s.maxPatternLength, p.size());
        s.searchers.emplace_back(p.cbegin(), p.cend());
    }
}

bool FileContains(State& s, Worker& w, const std::wstring& path)
{
    HANDLE f;
    {
        IoScope io(w);
        f = CreateFileW(ExtendedPath(path).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                        nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    }
    if (f == INVALID_HANDLE_VALUE) {
        s.errors++;
        return false;
    }

    const size_t chunk = size_t(1) << 20;
    const size_t overlap = s.maxPatternLength > 0 ? s.maxPatternLength - 1 : 0;
    std::vector<char> buf(chunk + overlap);
    size_t carry = 0;
    bool hit = false;

    while (!s.cancel) {
        DWORD read = 0;
        BOOL ok;
        {
            IoScope io(w);
            ok = ReadFile(f, buf.data() + carry, DWORD(chunk), &read, nullptr);
        }
        if (!ok) {
            s.errors++;
            break;
        }
        if (read == 0)
            break;

        size_t n = carry + read;
        if (!s.caseSensitive)
            for (size_t i = carry; i < n; ++i)
                if (buf[i] >= 'A' && buf[i] <= 'Z')
                    buf[i] = char(buf[i] + 32);

        const char* first = buf.data();
        const char* last = buf.data() + n;
        for (auto& srch : s.searchers) {
            if (srch(first, last).first != last) {
                hit = true;
                break;
            }
        }
        if (hit)
            break;

        carry = std::min(overlap, n);
        memmove(buf.data(), buf.data() + n - carry, carry);
    }
    CloseHandle(f);
    return hit;
}

void Emit(State& s, std::vector<FmEntry>& meta, std::vector<std::wstring>& paths)
{
    for (size_t i = 0; i < meta.size(); ++i)
        meta[i].name = paths[i].c_str();
    std::lock_guard<std::mutex> lk(s.cbMutex);
    if (!s.released && s.cb)
        s.cb(meta.data(), int(meta.size()), s.user);
}

void ProcessDirectory(State& s, Worker& w, const std::wstring& dir)
{
    {
        std::lock_guard<std::mutex> lk(s.currentMutex);
        s.currentDir = dir;
    }
    s.dirsScanned++;

    WIN32_FIND_DATAW fd;
    HANDLE h;
    {
        IoScope io(w);
        h = FindFirst(dir, fd);
    }
    if (h == INVALID_HANDLE_VALUE) {
        if (GetLastError() != ERROR_FILE_NOT_FOUND)
            s.errors++;
        return;
    }

    std::vector<FmEntry> hitMeta;
    std::vector<std::wstring> hitPaths;
    std::vector<std::wstring> subdirs;

    BOOL more = TRUE;
    while (more && !s.cancel) {
        if (!IsDots(fd.cFileName)) {
            const bool isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            const bool hidden = (fd.dwFileAttributes & FILE_ATTRIBUTE_HIDDEN) != 0;
            if (s.includeHidden || !hidden) {
                std::wstring full = JoinPath(dir, fd.cFileName);
                if (isDir) {
                    // 정션/심볼릭 링크는 따라가지 않는다 (순환 방지)
                    if (s.recursive && !(fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT))
                        subdirs.push_back(full);
                } else {
                    s.filesScanned++;
                }

                bool match = PathMatchSpecExW(fd.cFileName, s.mask.c_str(), PMSF_MULTIPLE) == S_OK;
                if (match && !s.text.empty())
                    match = !isDir && FileContains(s, w, full);
                if (match) {
                    hitMeta.push_back(MakeEntry(fd));
                    hitPaths.push_back(std::move(full));
                }
            }
        }
        {
            IoScope io(w);
            more = FindNextFileW(h, &fd);
        }
        if (!more && GetLastError() != ERROR_NO_MORE_FILES)
            s.errors++;
    }
    FindClose(h);

    if (!subdirs.empty()) {
        {
            std::lock_guard<std::mutex> lk(s.queueMutex);
            for (auto it = subdirs.rbegin(); it != subdirs.rend(); ++it)
                s.dirs.push_back(std::move(*it));
        }
        s.queueCv.notify_all();
    }
    if (!hitMeta.empty()) {
        s.found += long long(hitMeta.size());
        Emit(s, hitMeta, hitPaths);
    }
}

void WorkerLoop(std::shared_ptr<State> s, Worker* w)
{
    w->thread = static_cast<HANDLE>(FmOpenCurrentThread());
    for (;;) {
        std::wstring dir;
        {
            std::unique_lock<std::mutex> lk(s->queueMutex);
            s->queueCv.wait(lk, [&] { return s->cancel || !s->dirs.empty() || s->busy == 0; });
            if (s->cancel || s->dirs.empty()) {
                lk.unlock();
                s->queueCv.notify_all();
                return;
            }
            dir = std::move(s->dirs.back());
            s->dirs.pop_back();
            s->busy++;
        }
        ProcessDirectory(*s, *w, dir);
        {
            std::lock_guard<std::mutex> lk(s->queueMutex);
            s->busy--;
        }
        s->queueCv.notify_all();
    }
}

void Coordinator(std::shared_ptr<State> s)
{
    std::atomic<bool> done{ false };

    // 워치독: 한 번의 I/O 호출이 제한 시간을 넘거나 취소 요청이 오면 해당 스레드의 동기 I/O 를 취소한다.
    std::thread watchdog([&] {
        while (!done) {
            Sleep(200);
            ULONGLONG now = GetTickCount64();
            for (auto& w : s->workers) {
                ULONGLONG start = w->ioStart;
                HANDLE th = w->thread;
                if (!start || !th || w->cancelledStart == start)
                    continue;
                if (s->cancel || now - start > s->ioTimeout) {
                    w->cancelledStart = start;
                    CancelSynchronousIo(th);
                }
            }
        }
    });

    std::vector<std::thread> threads;
    for (auto& w : s->workers)
        threads.emplace_back(WorkerLoop, s, w.get());
    for (auto& t : threads)
        t.join();

    done = true;
    watchdog.join();
    s->running = false;
}

struct Handle {
    std::shared_ptr<State> state;
};

} // namespace search

FM_API void* FM_CALL FmSearchStart(const FmSearchParams* p, FmEntryBatchCallback cb, void* user)
{
    if (!p || !p->root || !cb)
        return nullptr;

    auto s = std::make_shared<search::State>();
    s->root = p->root;
    while (s->root.size() > 3 && s->root.back() == L'\\')
        s->root.pop_back();
    s->mask = (p->mask && *p->mask) ? p->mask : L"*";
    s->text = p->text ? p->text : L"";
    s->caseSensitive = p->caseSensitive != 0;
    s->recursive = p->recursive != 0;
    s->includeHidden = p->includeHidden != 0;
    s->threadCount = std::clamp(p->threadCount, 1, 16);
    s->ioTimeout = ULONGLONG(p->ioTimeoutMs > 0 ? p->ioTimeoutMs : 5000);
    s->cb = cb;
    s->user = user;
    search::BuildPatterns(*s);

    s->dirs.push_back(s->root);
    for (int i = 0; i < s->threadCount; ++i)
        s->workers.push_back(std::make_unique<search::Worker>());

    std::thread(search::Coordinator, s).detach();
    return new search::Handle{ s };
}

FM_API void FM_CALL FmSearchCancel(void* handle)
{
    if (!handle)
        return;
    auto& s = static_cast<search::Handle*>(handle)->state;
    {
        std::lock_guard<std::mutex> lk(s->queueMutex);
        s->cancel = true;
    }
    s->queueCv.notify_all();
}

FM_API int FM_CALL FmSearchGetStatus(void* handle, FmSearchStatus* st, wchar_t* currentDir, int currentDirLength)
{
    if (!handle || !st)
        return ERROR_INVALID_PARAMETER;
    auto& s = static_cast<search::Handle*>(handle)->state;
    st->running = s->running ? 1 : 0;
    st->reserved = 0;
    st->dirsScanned = s->dirsScanned;
    st->filesScanned = s->filesScanned;
    st->found = s->found;
    st->errors = s->errors;
    if (currentDir && currentDirLength > 0) {
        std::lock_guard<std::mutex> lk(s->currentMutex);
        wcsncpy_s(currentDir, size_t(currentDirLength), s->currentDir.c_str(), _TRUNCATE);
    }
    return 0;
}

FM_API void FM_CALL FmSearchFree(void* handle)
{
    if (!handle)
        return;
    auto* h = static_cast<search::Handle*>(handle);
    {
        // 이후로는 콜백을 호출하지 않는다. 남은 스레드는 shared_ptr 로 상태를 유지하다 스스로 종료한다.
        std::lock_guard<std::mutex> lk(h->state->cbMutex);
        h->state->released = true;
    }
    FmSearchCancel(handle);
    delete h;
}
