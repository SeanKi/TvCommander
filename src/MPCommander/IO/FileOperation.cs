using System.Runtime.InteropServices;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.IO;

public enum OpKind { Copy, Move }
public enum ConflictChoice { Overwrite, OverwriteAll, OverwriteOlderAll, Skip, SkipAll, Cancel }
public enum ErrorChoice { Retry, Skip, Cancel }

public sealed record ConflictInfo(string Source, string Destination, long SourceSize, DateTime SourceTime,
                                  long DestinationSize, DateTime DestinationTime);

/// <summary>
/// 복사/이동 엔진. 전용 스레드에서 실행된다.
///  - PC↔PC: C++ CopyFileEx / MoveFileWithProgress
///  - 휴대폰(mtp://)↔PC: C++ WPD 스트림 전송
///  - 휴대폰↔휴대폰: 임시 파일을 거쳐 전송
/// 작은 파일(1MB 미만, PC↔PC)은 여러 개를 동시에 복사한다 — 작은 파일은 파일마다 드는 고정 비용(열기·만들기·백신 검사)이
/// 대부분이라 동시에 하면 SSD 에서 약 3배, 네트워크에서는 더 빨라진다. 대상 확인과 진행 콜백도 생략하고("있으면 실패"로 복사),
/// 이미 있거나 실패한 파일만 끝에 차례로 다시 처리하며 덮어쓰기·오류를 묻는다.
/// UI 는 공개 필드를 주기적으로 읽어 진행률을 그린다.
/// </summary>
public sealed class FileOperation : ProgressOperation
{
    private sealed record WorkItem(string Src, string Dst, bool IsDir, long Size);
    private readonly record struct Info(bool Exists, bool IsDir, long Size, DateTime LastWrite);

    /// <summary>이보다 작은 파일은 동시 복사 (큰 파일은 차례로, 진행률을 보이며)</summary>
    private const long SmallFileLimit = 1024 * 1024;

    /// <summary>작은 파일이 이보다 적으면 동시 복사를 하지 않는다 (몇 개는 하나씩 해도 금방이고, 진행률이 보인다)</summary>
    private const int MinParallelFiles = 8;

    /// <summary>동시 복사 개수 (설정 CopyThreads). 0 = 디스크 종류에 따라 자동</summary>
    public static int ThreadSetting { get; set; }

    /// <summary>Auto FastCopy 사용 (도움이 될 때만 작은 파일 동시 복사). 끄면 예전처럼 파일마다 확인하며 하나씩 — 속도 비교용</summary>
    public bool FastMode { get; set; } = true;

    private readonly IReadOnlyList<string> _sources;
    private readonly string _destination;
    private readonly FmProgressCallback _progressCb;
    private long _bytesBase;
    private ConflictChoice? _conflictAll;

    public FileOperation(OpKind kind, IReadOnlyList<string> sources, string destination)
    {
        Kind = kind;
        _sources = sources;
        _destination = destination;
        _progressCb = OnProgress;
        Log = new OperationLog();
    }

    public OpKind Kind { get; }
    public override string Title => Kind == OpKind.Copy ? Loc.T("복사") : Loc.T("이동");
    public string? TargetDirectory { get; private set; }

    /// <summary>원본과 같은 폴더로 복사하면 오류 대신 "이름 - 복사본" 을 만든다 (붙여넣기용)</summary>
    public bool RenameCopiesInSameFolder { get; set; }

    /// <summary>"보고서.txt" → "보고서 - 복사본.txt", 있으면 "보고서 - 복사본 (2).txt" ...</summary>
    private static string UniqueCopyName(string dir, string name)
    {
        bool isDir = GetInfo(PathUtil.Combine(dir, name)).IsDir;
        int dot = isDir ? -1 : name.LastIndexOf('.');
        string stem = dot > 0 ? name.Substring(0, dot) : name;
        string ext = dot > 0 ? name.Substring(dot) : "";
        for (int n = 1; ; n++)
        {
            var candidate = PathUtil.Combine(dir, Loc.F($"{stem} - 복사본{(n == 1 ? "" : $" ({n})")}{ext}"));
            if (!GetInfo(candidate).Exists) return candidate;
        }
    }

    public override void Cancel()
    {
        base.Cancel();
        if (_sources.Any(PathUtil.IsVirtual) || PathUtil.IsVirtual(_destination)) VirtualFs.CancelAll();
    }

    private int OnProgress(ulong transferred, ulong total, IntPtr user)
    {
        CurrentDone = (long)transferred;
        DoneBytes = _bytesBase + (long)transferred;
        Touch();
        return IsCancelled ? 1 : 0;
    }

    protected override void Execute()
    {
        Touch();
        var (targetDir, newName) = ResolveDestination();
        TargetDirectory = targetDir;
        Log!.Open(Loc.F($"{Title}: {_sources.Count:N0}개 항목 → {targetDir}"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool cancelled = false;
        try
        {
            Run(targetDir, newName);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            throw;
        }
        finally
        {
            double secs = Math.Max(0.001, watch.Elapsed.TotalSeconds);
            Log.Close((cancelled || IsCancelled ? Loc.T("취소됨") : Loc.T("끝")) +
                      Loc.F($": 파일 {DoneFiles:N0} / {TotalFiles:N0},  {PathUtil.FormatSize(DoneBytes)},  건너뜀 {Skipped:N0},  오류 {Errors:N0},  {secs:N1}초 ({DoneFiles / secs:N0}개/s, {PathUtil.FormatSize((long)(DoneBytes / secs))}/s)"));
        }
    }

    private unsafe void Run(string targetDir, string? newName)
    {
        if (!Attempt(targetDir, () => CreateDir(targetDir))) return;

        // ── 1단계: 목록 수집 ──
        Phase = Loc.T("목록 수집 중");
        var work = new List<WorkItem>();
        foreach (var raw in _sources)
        {
            ThrowIfCancelled();
            var src = PathUtil.TrimEnd(raw);
            var name = newName ?? PathUtil.LastSegment(src);
            if (Compat.IsEmpty(name) || PathUtil.IsRoot(src)) { ReportError(src, Loc.T("드라이브나 기기 루트는 복사/이동할 수 없습니다.")); continue; }

            var dst = PathUtil.Combine(targetDir, name);
            if (PathUtil.Same(src, dst))
            {
                // 같은 폴더에 붙여넣기(복사): 탐색기처럼 "이름 - 복사본" 으로
                if (Kind == OpKind.Copy && RenameCopiesInSameFolder && newName == null) dst = UniqueCopyName(targetDir, name);
                else { ReportError(src, Loc.T("원본과 대상이 같습니다.")); continue; }
            }

            Info info = default;
            if (!Attempt(src, () => info = GetInfo(src))) continue;
            if (!info.Exists) { ReportError(src, Loc.T("원본이 없습니다.")); continue; }
            if (info.IsDir && PathUtil.IsUnder(dst, src)) { ReportError(src, Loc.T("폴더를 자기 자신의 하위 폴더로 복사/이동할 수 없습니다.")); continue; }

            // PC 같은 볼륨 이동 + 대상 없음 → 이름 변경 한 번으로 끝
            if (Kind == OpKind.Move && IsLocalSameVolume(src, dst) && !File.Exists(dst) && !Directory.Exists(dst))
            {
                Phase = Loc.T("이동 중");
                CurrentFile = src;
                Touch();
                int rc = NativeMethods.FmMoveFile(src, dst, 0, null, IntPtr.Zero, CancelPtr);
                if (rc == 0) { DoneFiles++; TotalFiles++; Log!.Add(Loc.T("이동"), src); continue; }
                ThrowIfCancelled();
            }

            work.Add(new WorkItem(src, dst, info.IsDir, info.Size));
            if (info.IsDir) Scan(src, dst, work);
        }

        // ── 2단계: 복사/이동 ──
        foreach (var w in work)
        {
            if (w.IsDir) continue;
            TotalFiles++;
            TotalBytes += w.Size;
        }
        Phase = Kind == OpKind.Copy ? Loc.T("복사 중") : Loc.T("이동 중");

        // 폴더를 먼저 모두 만든다 (부모가 자식보다 앞에 있다)
        foreach (var w in work)
        {
            ThrowIfCancelled();
            if (w.IsDir) Attempt(w.Dst, () => CreateDir(w.Dst));
        }

        // 작은 PC 파일은 동시에, 나머지(큰 파일·휴대폰·FTP·같은 볼륨 이동)는 차례로
        var files = work.Where(w => !w.IsDir).ToList();
        string firstSrc = files.Count > 0 ? files[0].Src : targetDir;
        // Auto FastCopy: 도움이 될 때만 — 작은 PC 파일이 충분히 많고, 디스크가 동시 복사에 맞을 때 (HDD·USB 메모리는 하나씩)
        int threads = !FastMode ? 1 : ThreadSetting > 0 ? ThreadSetting : DiskKind.AutoThreads(firstSrc, targetDir);
        var small = threads > 1 ? files.Where(CanCopyInParallel).ToList() : new List<WorkItem>();
        string why = !FastMode ? Loc.T("Auto FastCopy 끔")
                   : threads <= 1 ? Loc.F($"디스크가 동시 복사에 맞지 않음 (원본 {DiskKind.Of(firstSrc)}, 대상 {DiskKind.Of(targetDir)})")
                   : small.Count < MinParallelFiles ? Loc.F($"작은 파일이 {small.Count:N0}개뿐")
                   : "";
        if (why.Length > 0) small.Clear();
        var later = new List<WorkItem>();
        Log!.Add(why.Length == 0
            ? Loc.F($"방식: Auto FastCopy — 작은 파일 {small.Count:N0}개를 동시에 {threads}개씩, 나머지 {files.Count - small.Count:N0}개는 차례로  (원본 {DiskKind.Of(firstSrc)}, 대상 {DiskKind.Of(targetDir)})")
            : Loc.F($"방식: {files.Count:N0}개를 하나씩 — {why}"));
        if (small.Count > 0) CopyInParallel(small, threads, later, overwrite: false);
        ProcessLater(later, threads);
        var smallSet = new HashSet<WorkItem>(small);
        foreach (var w in files.Where(f => !smallSet.Contains(f)))
        {
            ThrowIfCancelled();
            TransferFile(w);
        }

        // ── 3단계: 이동이면 비워진 원본 폴더 제거 (자식부터) ──
        if (Kind == OpKind.Move)
        {
            Phase = Loc.T("정리 중");
            for (int i = work.Count - 1; i >= 0; i--)
            {
                if (!work[i].IsDir) continue;
                try { DeleteEmptyDir(work[i].Src); } catch { /* 건너뛴 파일이 남은 폴더 */ }
            }
        }
    }

    private void Scan(string srcRoot, string dstRoot, List<WorkItem> work)
    {
        var stack = new Stack<(string Src, string Dst)>();
        stack.Push((srcRoot, dstRoot));
        while (stack.Count > 0)
        {
            ThrowIfCancelled();
            var (s, d) = stack.Pop();
            CurrentFile = s;
            Touch();

            List<(string Name, long Size, bool IsDir, bool IsLink)> entries = new();
            if (!Attempt(s, () => entries = ListDir(s))) continue;

            foreach (var (name, size, isDir, isLink) in entries)
            {
                var sp = PathUtil.Combine(s, name);
                var dp = PathUtil.Combine(d, name);
                work.Add(new WorkItem(sp, dp, isDir, isDir ? 0 : size));
                if (isDir && !isLink) stack.Push((sp, dp));
            }
        }
    }

    private bool CanCopyInParallel(WorkItem w)
        => w.Size < SmallFileLimit && !PathUtil.IsVirtual(w.Src) && !PathUtil.IsVirtual(w.Dst)
           && !(Kind == OpKind.Move && IsLocalSameVolume(w.Src, w.Dst));   // 같은 볼륨 이동은 이름 바꾸기 한 번

    /// <summary>
    /// 작은 파일을 동시에 복사 ("있으면 실패", 진행 콜백 없음). 이미 있거나 실패한 파일은 later 에 모아
    /// 끝에 차례로 처리한다 (덮어쓰기·오류 질문은 한 번에 하나씩).
    /// </summary>
    private unsafe void CopyInParallel(List<WorkItem> items, int threads, List<WorkItem> later, bool overwrite)
    {
        var deferred = new System.Collections.Concurrent.ConcurrentQueue<WorkItem>();
        var undeleted = new System.Collections.Concurrent.ConcurrentQueue<WorkItem>();
        IntPtr cancel = (IntPtr)CancelPtr;
        string verb = overwrite ? Loc.T("덮어씀") : Kind == OpKind.Copy ? Loc.T("복사") : Loc.T("이동");
        CurrentSize = 0;
        CurrentDone = 0;

        Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = threads }, (w, state) =>
        {
            if (IsCancelled) { state.Stop(); return; }
            CurrentFile = w.Src;
            try
            {
                int rc = NativeMethods.FmCopyFile(w.Src, w.Dst, overwrite ? 1 : 0, null, IntPtr.Zero, (int*)cancel);
                if (rc != 0) { if (!IsCancelled) deferred.Enqueue(w); return; }
                if (Kind == OpKind.Move)
                {
                    try { DeleteFile(w.Src); }
                    catch { undeleted.Enqueue(w); }
                }
                Interlocked.Increment(ref DoneFiles);
                Interlocked.Add(ref DoneBytes, w.Size);
                Log!.Add(verb, w.Src);
                Touch();
            }
            catch
            {
                deferred.Enqueue(w);
            }
        });
        _bytesBase = Interlocked.Read(ref DoneBytes);
        ThrowIfCancelled();

        // 복사는 됐지만 원본을 못 지운 이동: 오류를 물으며 다시
        foreach (var w in undeleted)
        {
            ThrowIfCancelled();
            Attempt(w.Src, () => DeleteFile(w.Src));
        }
        // 원래 순서대로 (같은 폴더의 덮어쓰기 질문이 섞이지 않게)
        var set = new HashSet<WorkItem>(deferred);
        later.AddRange(items.Where(set.Contains));
        if (later.Count > 0 && !overwrite) Log!.Add(Loc.F($"이미 있거나 실패한 {later.Count:N0}개는 차례로 다시 처리합니다"));
    }

    /// <summary>
    /// 이미 있거나 실패한 작은 파일을 차례로 (덮어쓰기·오류를 묻는다). '모두 덮어쓰기' 로 답하면 남은 것은 다시 동시에 덮어쓴다.
    /// </summary>
    private void ProcessLater(List<WorkItem> later, int threads)
    {
        int i = 0;
        for (; i < later.Count && _conflictAll != ConflictChoice.OverwriteAll; i++)
        {
            ThrowIfCancelled();
            TransferFile(later[i]);
        }
        if (i >= later.Count) return;
        var rest = later.GetRange(i, later.Count - i);
        var again = new List<WorkItem>();
        Log!.Add(Loc.F($"모두 덮어쓰기: 남은 {rest.Count:N0}개를 동시에 덮어씁니다"));
        CopyInParallel(rest, threads, again, overwrite: true);
        foreach (var w in again)
        {
            ThrowIfCancelled();
            TransferFile(w);
        }
    }

    private void TransferFile(WorkItem w)
    {
        CurrentFile = w.Src;
        CurrentSize = w.Size;
        CurrentDone = 0;
        Touch();

        try
        {
            bool overwrite = false;
            Info dst = default;
            if (!Attempt(w.Dst, () => dst = GetInfo(w.Dst))) return;
            if (dst.Exists && dst.IsDir) { ReportError(w.Dst, Loc.T("같은 이름의 폴더가 이미 있습니다.")); return; }
            if (dst.Exists && !ResolveConflict(w, dst, out overwrite)) { Skipped++; Log!.Add(Loc.T("건너뜀"), w.Src, Loc.T("대상에 이미 있음")); return; }

            bool rename = Kind == OpKind.Move && IsLocalSameVolume(w.Src, w.Dst);
            bool ok = Attempt(w.Src, () =>
            {
                CurrentDone = 0;
                DoneBytes = _bytesBase;
                if (rename) MoveLocal(w.Src, w.Dst, overwrite);
                else CopyFile(w.Src, w.Dst, overwrite);
            });
            if (!ok) return;

            if (Kind == OpKind.Move && !rename) Attempt(w.Src, () => DeleteFile(w.Src));
            DoneFiles++;
            Log!.Add(overwrite ? Loc.T("덮어씀") : Kind == OpKind.Copy ? Loc.T("복사") : Loc.T("이동"), w.Src);
        }
        finally
        {
            _bytesBase += w.Size;
            DoneBytes = _bytesBase;
            CurrentDone = 0;
            Touch();
        }
    }

    private bool ResolveConflict(WorkItem w, Info dst, out bool overwrite)
    {
        overwrite = false;
        var src = GetInfo(w.Src);
        var choice = _conflictAll ?? AskConflict(new ConflictInfo(w.Src, w.Dst, src.Size, src.LastWrite, dst.Size, dst.LastWrite));
        Touch();
        switch (choice)
        {
            case ConflictChoice.OverwriteAll:
            case ConflictChoice.SkipAll:
            case ConflictChoice.OverwriteOlderAll:
                _conflictAll = choice;
                break;
            case ConflictChoice.Cancel:
                Cancel();
                throw new OperationCanceledException();
        }
        overwrite = choice switch
        {
            ConflictChoice.Overwrite or ConflictChoice.OverwriteAll => true,
            ConflictChoice.OverwriteOlderAll => src.LastWrite > dst.LastWrite,
            _ => false,
        };
        return overwrite;
    }

    // ───────────────────────── 경로 종류별 기본 동작 (PC / 휴대폰·FTP·WebDAV) ─────────────────────────

    private static bool IsLocalSameVolume(string a, string b)
        => !PathUtil.IsVirtual(a) && !PathUtil.IsVirtual(b) && PathUtil.SameVolume(a, b);

    /// <summary>원격 전송 진행 보고 (true = 취소)</summary>
    private bool RemoteProgress(long done, long total)
    {
        OnProgress((ulong)Math.Max(0, done), (ulong)Math.Max(0, total), IntPtr.Zero);
        return IsCancelled;
    }

    private static Info GetInfo(string path)
    {
        if (PathUtil.IsVirtual(path))
        {
            var e = VirtualFs.For(path).GetEntry(path);
            if (e == null) return default;
            var time = e.LastWrite > 0 ? DateTime.FromFileTimeUtc(e.LastWrite).ToLocalTime() : DateTime.MinValue;
            return new Info(true, e.IsFolder, e.Size, time);
        }
        if (Directory.Exists(path)) return new Info(true, true, 0, Directory.GetLastWriteTime(path));
        var fi = new FileInfo(path);
        return fi.Exists ? new Info(true, false, fi.Length, fi.LastWriteTime) : default;
    }

    private unsafe List<(string Name, long Size, bool IsDir, bool IsLink)> ListDir(string dir)
    {
        var result = new List<(string, long, bool, bool)>();
        if (PathUtil.IsVirtual(dir))
        {
            foreach (var e in VirtualFs.For(dir).List(dir, null))
            {
                ThrowIfCancelled();
                result.Add((e.Name, e.Size, e.IsFolder, false));
            }
            Touch();
            return result;
        }

        FmEntryBatchCallback cb = (e, n, _) =>
        {
            for (int i = 0; i < n; i++)
            {
                uint a = e[i].Attributes;
                result.Add((Marshal.PtrToStringUni(e[i].Name)!, (long)e[i].Size,
                    (a & (uint)FileAttributes.Directory) != 0, (a & (uint)FileAttributes.ReparsePoint) != 0));
            }
            Touch();
            return IsCancelled ? 0 : 1;
        };
        int rc = NativeMethods.FmEnumDirectory(dir, cb, IntPtr.Zero, CancelPtr, null);
        GC.KeepAlive(cb);
        if (rc != 0) throw Win32Errors.ToIOException(rc, dir);
        return result;
    }

    private static void CreateDir(string path)
    {
        if (PathUtil.IsVirtual(path)) VirtualFs.For(path).EnsureFolder(path);
        else Directory.CreateDirectory(path);
    }

    private unsafe void MoveLocal(string src, string dst, bool overwrite)
    {
        int rc = NativeMethods.FmMoveFile(src, dst, overwrite ? 1 : 0, null, IntPtr.Zero, CancelPtr);
        if (rc != 0)
        {
            ThrowIfCancelled();
            throw Win32Errors.ToIOException(rc, src);
        }
    }

    private unsafe void CopyFile(string src, string dst, bool overwrite)
    {
        bool srcRemote = PathUtil.IsVirtual(src), dstRemote = PathUtil.IsVirtual(dst);

        if (!srcRemote && !dstRemote)
        {
            int rc = NativeMethods.FmCopyFile(src, dst, overwrite ? 1 : 0, _progressCb, IntPtr.Zero, CancelPtr);
            if (rc != 0)
            {
                ThrowIfCancelled();
                throw Win32Errors.ToIOException(rc, src);
            }
            return;
        }

        if (srcRemote && !dstRemote)
        {
            if (overwrite) ClearReadOnly(dst);
            VirtualFs.For(src).Download(src, dst, overwrite, RemoteProgress);
            return;
        }

        if (!srcRemote && dstRemote)
        {
            VirtualFs.For(dst).Upload(src, dst, overwrite, RemoteProgress);
            return;
        }

        // 원격 → 원격 (폰↔폰, 폰↔FTP, FTP↔WebDAV ...): PC 임시 파일 경유
        var temp = Path.Combine(Path.GetTempPath(), "MP-Commander", "xfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            VirtualFs.For(src).Download(src, temp, true, RemoteProgress);
            VirtualFs.For(dst).Upload(temp, dst, overwrite, RemoteProgress);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var a = File.GetAttributes(path);
        if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
    }

    private static void DeleteFile(string path)
    {
        if (PathUtil.IsVirtual(path))
        {
            VirtualFs.For(path).Delete(path, recursive: false);
            return;
        }
        ClearReadOnly(path);
        File.Delete(path);
    }

    private static void DeleteEmptyDir(string path)
    {
        if (PathUtil.IsVirtual(path))
        {
            var fs = VirtualFs.For(path);
            if (fs.List(path, null).Count == 0) fs.Delete(path, recursive: false);
            return;
        }
        Directory.Delete(path, false);
    }

    /// <summary>
    /// 입력이 구분자로 끝나거나 존재하는 폴더면 그 안으로, 아니면 (단일 항목일 때) 새 이름으로 해석한다.
    /// </summary>
    private (string TargetDir, string? NewName) ResolveDestination()
    {
        var raw = Environment.ExpandEnvironmentVariables(_destination.Trim().Trim('"'));
        bool endsWithSlash = raw.EndsWith('\\') || raw.EndsWith('/');
        var input = PathUtil.Normalize(raw, PathUtil.Parent(PathUtil.TrimEnd(_sources[0])));

        bool isDir = PathUtil.IsVirtual(input)
            ? PathUtil.IsRoot(input) || VirtualFs.For(input).GetEntry(input)?.IsFolder == true
            : Directory.Exists(input);
        if (endsWithSlash || isDir)
            return (input, null);
        if (_sources.Count == 1)
            return (PathUtil.Parent(input)!, PathUtil.LastSegment(input));
        return (input, null);
    }
}
