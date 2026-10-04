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
/// UI 는 공개 필드를 주기적으로 읽어 진행률을 그린다.
/// </summary>
public sealed class FileOperation : ProgressOperation
{
    private sealed record WorkItem(string Src, string Dst, bool IsDir, long Size);
    private readonly record struct Info(bool Exists, bool IsDir, long Size, DateTime LastWrite);

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
    }

    public OpKind Kind { get; }
    public override string Title => Kind == OpKind.Copy ? "복사" : "이동";
    public string? TargetDirectory { get; private set; }

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

    protected override unsafe void Execute()
    {
        Touch();
        var (targetDir, newName) = ResolveDestination();
        TargetDirectory = targetDir;
        if (!Attempt(targetDir, () => CreateDir(targetDir))) return;

        // ── 1단계: 목록 수집 ──
        Phase = "목록 수집 중";
        var work = new List<WorkItem>();
        foreach (var raw in _sources)
        {
            ThrowIfCancelled();
            var src = PathUtil.TrimEnd(raw);
            var name = newName ?? PathUtil.LastSegment(src);
            if (Compat.IsEmpty(name) || PathUtil.IsRoot(src)) { ReportError(src, "드라이브나 기기 루트는 복사/이동할 수 없습니다."); continue; }

            var dst = PathUtil.Combine(targetDir, name);
            if (PathUtil.Same(src, dst)) { ReportError(src, "원본과 대상이 같습니다."); continue; }

            Info info = default;
            if (!Attempt(src, () => info = GetInfo(src))) continue;
            if (!info.Exists) { ReportError(src, "원본이 없습니다."); continue; }
            if (info.IsDir && PathUtil.IsUnder(dst, src)) { ReportError(src, "폴더를 자기 자신의 하위 폴더로 복사/이동할 수 없습니다."); continue; }

            // PC 같은 볼륨 이동 + 대상 없음 → 이름 변경 한 번으로 끝
            if (Kind == OpKind.Move && IsLocalSameVolume(src, dst) && !File.Exists(dst) && !Directory.Exists(dst))
            {
                Phase = "이동 중";
                CurrentFile = src;
                Touch();
                int rc = NativeMethods.FmMoveFile(src, dst, 0, null, IntPtr.Zero, CancelPtr);
                if (rc == 0) { DoneFiles++; TotalFiles++; continue; }
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
        Phase = Kind == OpKind.Copy ? "복사 중" : "이동 중";

        foreach (var w in work)
        {
            ThrowIfCancelled();
            if (w.IsDir)
                Attempt(w.Dst, () => CreateDir(w.Dst));
            else
                TransferFile(w);
        }

        // ── 3단계: 이동이면 비워진 원본 폴더 제거 (자식부터) ──
        if (Kind == OpKind.Move)
        {
            Phase = "정리 중";
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
            if (dst.Exists && dst.IsDir) { ReportError(w.Dst, "같은 이름의 폴더가 이미 있습니다."); return; }
            if (dst.Exists && !ResolveConflict(w, dst, out overwrite)) { Skipped++; return; }

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
