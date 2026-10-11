// FmText.cpp - 큰 텍스트 파일 뷰어 지원: 줄 색인(백그라운드), 임의 위치 읽기, 바이트 단위 빠른 찾기.
// 수백 MB ~ 수 GB 로그를 메모리에 다 올리지 않고 보이는 만큼만 읽게 해 준다.
#include "FmCore.h"

#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <functional>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <string>
#include <thread>
#include <vector>

namespace {

constexpr DWORD kBlock = 4u << 20;   // 한 번에 읽는 크기

std::wstring Extended(const std::wstring& p)
{
    if (p.size() < MAX_PATH - 12 || p.rfind(L"\\\\?\\", 0) == 0)
        return p;
    if (p.rfind(L"\\\\", 0) == 0)
        return L"\\\\?\\UNC\\" + p.substr(2);
    return L"\\\\?\\" + p;
}

HANDLE OpenShared(const std::wstring& path)
{
    return CreateFileW(Extended(path).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                       nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
}

// 지정 위치에서 읽기 (동기 핸들 + OVERLAPPED 오프셋)
bool ReadAt(HANDLE h, int64_t offset, void* buf, DWORD len, DWORD* read)
{
    OVERLAPPED ov{};
    ov.Offset = DWORD(uint64_t(offset) & 0xFFFFFFFFu);
    ov.OffsetHigh = DWORD(uint64_t(offset) >> 32);
    *read = 0;
    if (ReadFile(h, buf, len, read, &ov))
        return true;
    return GetLastError() == ERROR_HANDLE_EOF;
}

struct TextFile {
    std::wstring path;
    int unit = 1;
    bool bigEndian = false;
    int64_t start = 0;

    HANDLE readHandle = INVALID_HANDLE_VALUE;
    std::mutex readMutex;

    std::shared_mutex indexMutex;
    std::vector<int64_t> lines;   // 줄 시작 위치 (lines[0] = start)
    std::atomic<int64_t> indexed{ 0 };
    std::atomic<int64_t> fileSize{ 0 };
    std::atomic<bool> done{ false };
    std::atomic<bool> cancel{ false };
    std::atomic<int> error{ 0 };

    ~TextFile()
    {
        if (readHandle != INVALID_HANDLE_VALUE)
            CloseHandle(readHandle);
    }

    // 블록 안에서 줄 바꿈 다음 위치를 모은다. base = 블록 첫 바이트의 파일 위치
    void ScanNewlines(const uint8_t* b, size_t n, int64_t base, std::vector<int64_t>& out) const
    {
        if (unit == 1) {
            const uint8_t* p = b;
            const uint8_t* end = b + n;
            while (p < end) {
                auto* q = static_cast<const uint8_t*>(memchr(p, '\n', size_t(end - p)));
                if (!q)
                    break;
                out.push_back(base + (q - b) + 1);
                p = q + 1;
            }
        } else {
            // UTF-16: 0x0A 바이트를 memchr 로 찾고, 글자 경계(LE 는 짝수, BE 는 홀수 자리)와 짝 바이트 0 을 확인
            const uint8_t* p = b;
            const uint8_t* end = b + n;
            const bool baseOdd = ((base - start) & 1) != 0;
            while (p < end) {
                auto* q = static_cast<const uint8_t*>(memchr(p, 0x0A, size_t(end - p)));
                if (!q)
                    break;
                size_t i = size_t(q - b);
                bool odd = ((i & 1) != 0) != baseOdd;
                if (!bigEndian && !odd && i + 1 < n && b[i + 1] == 0)
                    out.push_back(base + int64_t(i) + 2);
                else if (bigEndian && odd && i >= 1 && b[i - 1] == 0)
                    out.push_back(base + int64_t(i) + 1);
                p = q + 1;
            }
        }
    }
};

void IndexThread(std::shared_ptr<TextFile> t)
{
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);   // UI 가 먼저
    HANDLE h = OpenShared(t->path);
    if (h == INVALID_HANDLE_VALUE) {
        t->error = int(GetLastError());
        t->done = true;
        return;
    }
    LARGE_INTEGER size{};
    GetFileSizeEx(h, &size);
    t->fileSize = size.QuadPart;

    std::vector<uint8_t> buf(kBlock);
    std::vector<int64_t> found;
    int64_t pos = t->start;
    bool first = true;
    while (!t->cancel && pos < size.QuadPart) {
        // 첫 읽기는 작게: 첫 화면에 필요한 줄을 바로 내놓는다
        DWORD want = DWORD(std::min<int64_t>(first ? (256 << 10) : kBlock, size.QuadPart - pos));
        first = false;
        DWORD read = 0;
        if (!ReadAt(h, pos, buf.data(), want, &read)) {
            t->error = int(GetLastError());
            break;
        }
        if (read == 0)
            break;
        if (t->unit == 2)
            read &= ~DWORD(1);
        found.clear();
        t->ScanNewlines(buf.data(), read, pos, found);
        if (!found.empty()) {
            std::unique_lock<std::shared_mutex> lk(t->indexMutex);
            t->lines.insert(t->lines.end(), found.begin(), found.end());
        }
        pos += read;
        t->indexed = pos;
    }
    CloseHandle(h);
    t->done = true;
}

struct Handle {
    std::shared_ptr<TextFile> file;
};

// ───────── 찾기 ─────────

struct Search {
    std::shared_ptr<TextFile> file;
    std::vector<uint8_t> pattern;
    bool caseInsensitive = false;
    FmTextHitCallback cb = nullptr;
    void* user = nullptr;
    std::mutex cbMutex;
    bool released = false;
    std::atomic<bool> cancel{ false };
    std::atomic<int64_t> doneBytes{ 0 };
    std::atomic<bool> done{ false };
};

inline uint8_t Lower(uint8_t c) { return (c >= 'A' && c <= 'Z') ? uint8_t(c + 32) : c; }

// ASCII 만 소문자로 (UTF-16 은 상위 바이트가 0 인 글자의 하위 바이트만)
void LowerAscii(uint8_t* b, size_t n, int unit, bool bigEndian)
{
    if (unit == 1) {
        for (size_t i = 0; i < n; ++i)
            b[i] = Lower(b[i]);
    } else if (!bigEndian) {
        for (size_t i = 0; i + 1 < n; i += 2)
            if (b[i + 1] == 0)
                b[i] = Lower(b[i]);
    } else {
        for (size_t i = 0; i + 1 < n; i += 2)
            if (b[i] == 0)
                b[i + 1] = Lower(b[i + 1]);
    }
}

// 일반 바이트 패턴 찾기 (첫 바이트로 memchr 후 비교 — 짧은 패턴에 빠르다)
const uint8_t* FindBytes(const uint8_t* p, const uint8_t* end, const uint8_t* pat, size_t m)
{
    if (m == 0 || size_t(end - p) < m)
        return nullptr;
    const uint8_t first = pat[0];
    const uint8_t* last = end - m;
    while (p <= last) {
        p = static_cast<const uint8_t*>(memchr(p, first, size_t(last - p) + 1));
        if (!p)
            return nullptr;
        if (memcmp(p + 1, pat + 1, m - 1) == 0)
            return p;
        ++p;
    }
    return nullptr;
}

void SearchThread(std::shared_ptr<Search> s)
{
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
    auto& t = *s->file;
    HANDLE h = OpenShared(t.path);
    if (h == INVALID_HANDLE_VALUE) {
        s->done = true;
        return;
    }
    LARGE_INTEGER size{};
    GetFileSizeEx(h, &size);

    const size_t m = s->pattern.size();
    const int unit = t.unit;
    const size_t carry = ((m > 0 ? m - 1 : 0) + unit - 1) / unit * unit;   // 블록 경계에 걸친 일치용
    std::vector<uint8_t> buf(kBlock + carry);
    std::vector<int64_t> lines;
    std::vector<int32_t> counts;
    ULONGLONG lastReport = GetTickCount64();

    auto flush = [&](bool force) {
        if (lines.empty() && !force)
            return true;
        std::lock_guard<std::mutex> lk(s->cbMutex);
        if (s->released)
            return false;
        int ok = s->cb(lines.data(), counts.data(), int(lines.size()), s->doneBytes, s->user);
        lines.clear();
        counts.clear();
        lastReport = GetTickCount64();
        return ok != 0;
    };

    int64_t lineNo = 0;           // countedUpTo 까지의 줄 번호
    int64_t countedUpTo = t.start;
    int64_t dataBase = t.start;   // buf[0] 의 파일 위치
    size_t have = 0;
    int64_t pos = t.start;
    int64_t lastLine = -1;

    // [from, to) 파일 위치 사이의 줄 바꿈 수 (buf 는 dataBase 부터)
    auto countNewlines = [&](const uint8_t* b, int64_t from, int64_t to) {
        int64_t n = 0;
        size_t i = size_t(from - dataBase), e = size_t(to - dataBase);
        if (unit == 1) {
            const uint8_t* p = b + i;
            const uint8_t* end = b + e;
            while (p < end) {
                p = static_cast<const uint8_t*>(memchr(p, '\n', size_t(end - p)));
                if (!p)
                    break;
                ++n;
                ++p;
            }
        } else {
            const uint8_t* p = b + i;
            const uint8_t* end = b + e;
            while (p < end) {
                p = static_cast<const uint8_t*>(memchr(p, 0x0A, size_t(end - p)));
                if (!p)
                    break;
                size_t k = size_t(p - b);
                bool odd = ((dataBase + int64_t(k) - t.start) & 1) != 0;
                if (!t.bigEndian ? (!odd && k + 1 < have && b[k + 1] == 0) : (odd && k >= 1 && b[k - 1] == 0))
                    ++n;
                ++p;
            }
        }
        return n;
    };

    bool stop = false;
    while (!s->cancel && !stop) {
        DWORD want = DWORD(std::min<int64_t>(kBlock, size.QuadPart - pos));
        DWORD read = 0;
        if (want > 0 && !ReadAt(h, pos, buf.data() + have, want, &read))
            break;
        const bool eof = read == 0;
        if (unit == 2)
            read &= ~DWORD(1);
        if (s->caseInsensitive)
            LowerAscii(buf.data() + have, read, unit, t.bigEndian);
        have += read;
        pos += read;

        // 이번에 일치의 '시작'으로 인정할 범위: [0, limit). 끝이 아니면 마지막 carry 바이트는 다음 블록과 함께 본다
        const size_t limit = eof ? have : (have > carry ? have - carry : 0);
        const uint8_t* b = buf.data();
        const uint8_t* searchEnd = b + std::min(have, limit + (m > 0 ? m - 1 : 0));
        const uint8_t* p = b;
        while (!s->cancel) {
            const uint8_t* q = FindBytes(p, searchEnd, s->pattern.data(), m);
            if (!q)
                break;
            size_t off = size_t(q - b);
            if (unit == 2 && ((dataBase + int64_t(off) - t.start) & 1)) {   // UTF-16 글자 경계가 아니면 무시
                p = q + 1;
                continue;
            }
            int64_t at = dataBase + int64_t(off);
            lineNo += countNewlines(b, countedUpTo, at);
            countedUpTo = at;
            if (lineNo == lastLine) {
                counts.back()++;
            } else {
                lines.push_back(lineNo);
                counts.push_back(1);
                lastLine = lineNo;
            }
            p = q + m;
        }
        if (eof)
            break;

        // 본 부분의 줄 바꿈을 세고 carry 만 남긴다
        int64_t keepFrom = dataBase + int64_t(limit);
        if (countedUpTo < keepFrom) {
            lineNo += countNewlines(b, countedUpTo, keepFrom);
            countedUpTo = keepFrom;
        }
        memmove(buf.data(), buf.data() + limit, have - limit);
        have -= limit;
        dataBase = keepFrom;
        s->doneBytes = dataBase;

        if (lines.size() >= 4096 || GetTickCount64() - lastReport >= 200)
            if (!flush(false))
                stop = true;
    }
    CloseHandle(h);
    s->doneBytes = size.QuadPart;
    if (!s->cancel)
        flush(true);
    s->done = true;
}

} // namespace

FM_API void* FM_CALL FmTextOpen(const wchar_t* path, int unit, int bigEndian, int64_t dataStart)
{
    if (!path)
        return nullptr;
    auto t = std::make_shared<TextFile>();
    t->path = path;
    t->unit = unit == 2 ? 2 : 1;
    t->bigEndian = bigEndian != 0;
    t->start = std::max<int64_t>(0, dataStart);
    t->lines.push_back(t->start);
    t->readHandle = CreateFileW(Extended(t->path).c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                nullptr, OPEN_EXISTING, FILE_FLAG_RANDOM_ACCESS, nullptr);
    if (t->readHandle == INVALID_HANDLE_VALUE)
        return nullptr;
    std::thread(IndexThread, t).detach();
    return new Handle{ t };
}

FM_API void FM_CALL FmTextClose(void* text)
{
    if (!text)
        return;
    auto* h = static_cast<Handle*>(text);
    h->file->cancel = true;   // 색인 스레드는 shared_ptr 로 버티다 스스로 끝난다
    delete h;
}

FM_API int FM_CALL FmTextGetStatus(void* text, FmTextStatus* st)
{
    if (!text || !st)
        return ERROR_INVALID_PARAMETER;
    auto& t = *static_cast<Handle*>(text)->file;
    std::shared_lock<std::shared_mutex> lk(t.indexMutex);
    bool done = t.done;
    int64_t size = t.fileSize;
    int64_t n = int64_t(t.lines.size());
    // 끝나지 않았으면 마지막 줄은 아직 끝을 모른다. 끝났고 파일이 줄 바꿈으로 끝나면 빈 마지막 줄은 세지 않는다
    if (!done)
        n -= 1;
    else if (n > 1 && t.lines.back() >= size)
        n -= 1;
    st->lineCount = std::max<int64_t>(0, n);
    st->indexedBytes = t.indexed;
    st->fileSize = size;
    st->done = done ? 1 : 0;
    st->error = t.error;
    return 0;
}

FM_API int FM_CALL FmTextLineOffsets(void* text, int64_t first, int count, int64_t* offsets)
{
    if (!text || !offsets || first < 0 || count <= 0)
        return 0;
    auto& t = *static_cast<Handle*>(text)->file;
    std::shared_lock<std::shared_mutex> lk(t.indexMutex);
    int64_t total = int64_t(t.lines.size());
    if (first >= total)
        return 0;
    int64_t n = std::min<int64_t>(count, total - first);
    for (int64_t i = 0; i < n; ++i)
        offsets[i] = t.lines[size_t(first + i)];
    if (first + n < total) {
        offsets[n] = t.lines[size_t(first + n)];
    } else if (t.done) {
        offsets[n] = t.fileSize;
        if (n > 0 && offsets[n - 1] >= t.fileSize)   // 줄 바꿈으로 끝나는 파일의 빈 마지막 줄
            --n, offsets[n] = t.fileSize;
    } else {
        --n;   // 마지막 줄은 아직 끝을 모른다
        if (n > 0)
            offsets[n] = t.lines[size_t(first + n)];
    }
    return int(std::max<int64_t>(0, n));
}

FM_API int64_t FM_CALL FmTextLineOfOffset(void* text, int64_t offset)
{
    if (!text)
        return -1;
    auto& t = *static_cast<Handle*>(text)->file;
    std::shared_lock<std::shared_mutex> lk(t.indexMutex);
    auto it = std::upper_bound(t.lines.begin(), t.lines.end(), offset);
    return int64_t(it - t.lines.begin()) - 1;
}

FM_API int FM_CALL FmTextRead(void* text, int64_t offset, int length, uint8_t* buffer)
{
    if (!text || !buffer || length < 0 || offset < 0)
        return -int(ERROR_INVALID_PARAMETER);
    auto& t = *static_cast<Handle*>(text)->file;
    std::lock_guard<std::mutex> lk(t.readMutex);
    DWORD total = 0;
    while (total < DWORD(length)) {
        DWORD read = 0;
        if (!ReadAt(t.readHandle, offset + total, buffer + total, DWORD(length) - total, &read))
            return -int(GetLastError());
        if (read == 0)
            break;
        total += read;
    }
    return int(total);
}

FM_API void* FM_CALL FmTextSearchStart(void* text, const uint8_t* pattern, int patternLength, int caseInsensitive,
                                       FmTextHitCallback cb, void* user)
{
    if (!text || !pattern || patternLength <= 0 || !cb)
        return nullptr;
    auto s = std::make_shared<Search>();
    s->file = static_cast<Handle*>(text)->file;
    s->pattern.assign(pattern, pattern + patternLength);
    s->caseInsensitive = caseInsensitive != 0;
    if (s->caseInsensitive)
        LowerAscii(s->pattern.data(), s->pattern.size(), s->file->unit, s->file->bigEndian);
    s->cb = cb;
    s->user = user;
    std::thread(SearchThread, s).detach();
    return new std::shared_ptr<Search>(s);
}

FM_API int FM_CALL FmTextSearchGetStatus(void* search, int64_t* doneBytes, int* done)
{
    if (!search)
        return ERROR_INVALID_PARAMETER;
    auto& s = *static_cast<std::shared_ptr<Search>*>(search);
    if (doneBytes)
        *doneBytes = s->doneBytes;
    if (done)
        *done = s->done ? 1 : 0;
    return 0;
}

FM_API void FM_CALL FmTextSearchFree(void* search)
{
    if (!search)
        return;
    auto* sp = static_cast<std::shared_ptr<Search>*>(search);
    {
        std::lock_guard<std::mutex> lk((*sp)->cbMutex);
        (*sp)->released = true;
    }
    (*sp)->cancel = true;
    delete sp;
}
