using System.Runtime.InteropServices;
using TvCommander.Model;
using TvCommander.Native;

namespace TvCommander.IO;

public enum OpKind { Copy, Move }
public enum ConflictChoice { Overwrite, OverwriteAll, OverwriteOlderAll, Skip, SkipAll, Cancel }
public enum ErrorChoice { Retry, Skip, Cancel }

public sealed record ConflictInfo(string Source, string Destination, long SourceSize, DateTime SourceTime,
                                  long DestinationSize, DateTime DestinationTime);

/// <summary>
/// 복사/이동 엔진. 전용 스레드에서 실행되며 실제 파일 복사는 C++ (CopyFileEx) 이 담당한다.
/// UI 는 공개 필드를 주기적으로 읽어 진행률을 그린다.
/// </summary>
public sealed class FileOperation
{
    private sealed record WorkItem(string Src, string Dst, bool IsDir, long Size);

    private readonly IReadOnlyList<string> _sources;
    private readonly string _destination;
    private readonly int[] _cancel = GC.AllocateArray<int>(1, pinned: true);
    private readonly object _sync = new();
    private readonly FmProgressCallback _progressCb;
    private IntPtr _thread;
    private long _bytesBase;
    private ConflictChoice? _conflictAll;

    // ── 진행 상태 (UI 가 읽음) ──
    public volatile string Phase = "준비 중";
    public volatile string CurrentFile = "";
    public long TotalBytes, DoneBytes, CurrentSize, CurrentDone;
    public int TotalFiles, DoneFiles, Skipped, Errors;
    public long LastProgressTick = Environment.TickCount64;

    public FileOperation(OpKind kind, IReadOnlyList<string> sources, string destination)
    {
        Kind = kind;
        _sources = sources;
        _destination = destination;
        _progressCb = OnProgress;
    }

    public OpKind Kind { get; }
    public string Title => Kind == OpKind.Copy ? "복사" : "이동";
    public string? TargetDirectory { get; private set; }
    public bool IsCancelled => Volatile.Read(ref _cancel[0]) != 0;

    public Func<ConflictInfo, ConflictChoice> AskConflict { get; set; } = _ => ConflictChoice.Skip;
    public Func<string, string, ErrorChoice> AskError { get; set; } = (_, _) => ErrorChoice.Skip;

    private unsafe int* CancelPtr => (int*)Marshal.UnsafeAddrOfPinnedArrayElement(_cancel, 0);

    public void Cancel()
    {
        Volatile.Write(ref _cancel[0], 1);
        lock (_sync)
        {
            if (_thread != IntPtr.Zero) NativeMethods.FmCancelThreadIo(_thread);
        }
    }

    public Task RunAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            IntPtr h = NativeMethods.FmOpenCurrentThread();
            lock (_sync) _thread = h;
            try { Execute(); tcs.TrySetResult(); }
            catch (OperationCanceledException) { tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally
            {
                lock (_sync) _thread = IntPtr.Zero;
                NativeMethods.FmCloseHandle(h);
            }
        })
        { IsBackground = true, Name = "FileOperation" };
        t.Start();
        return tcs.Task;
    }

    private void Touch() => Interlocked.Exchange(ref LastProgressTick, Environment.TickCount64);

    private void ThrowIfCancelled()
    {
        if (IsCancelled) throw new OperationCanceledException();
    }

    private int OnProgress(ulong transferred, ulong total, IntPtr user)
    {
        CurrentDone = (long)transferred;
        DoneBytes = _bytesBase + (long)transferred;
        Touch();
        return IsCancelled ? 1 : 0;
    }

    private unsafe void Execute()
    {
        Touch();
        var (targetDir, newName) = ResolveDestination();
        TargetDirectory = targetDir;
        if (!Attempt(targetDir, () => Directory.CreateDirectory(targetDir))) return;

        // ── 1단계: 목록 수집 ──
        Phase = "목록 수집 중";
        var work = new List<WorkItem>();
        foreach (var raw in _sources)
        {
            ThrowIfCancelled();
            var src = PathUtil.TrimEnd(raw);
            var name = newName ?? Path.GetFileName(src);
            if (string.IsNullOrEmpty(name)) { ReportError(src, "드라이브 루트는 복사/이동할 수 없습니다."); continue; }

            var dst = Path.Combine(targetDir, name);
            if (PathUtil.Same(src, dst)) { ReportError(src, "원본과 대상이 같습니다."); continue; }

            FileAttributes attr = 0;
            long size = 0;
            if (!Attempt(src, () =>
                {
                    var fi = new FileInfo(src);
                    attr = fi.Attributes;
                    if (!attr.HasFlag(FileAttributes.Directory)) size = fi.Length;
                })) continue;

            bool isDir = attr.HasFlag(FileAttributes.Directory);
            if (isDir && PathUtil.IsUnder(dst, src)) { ReportError(src, "폴더를 자기 자신의 하위 폴더로 복사/이동할 수 없습니다."); continue; }

            // 같은 볼륨 이동 + 대상 없음 → 이름 변경 한 번으로 끝
            if (Kind == OpKind.Move && PathUtil.SameVolume(src, dst) && !File.Exists(dst) && !Directory.Exists(dst))
            {
                Phase = "이동 중";
                CurrentFile = src;
                Touch();
                int rc = NativeMethods.FmMoveFile(src, dst, 0, null, IntPtr.Zero, CancelPtr);
                if (rc == 0) { DoneFiles++; TotalFiles++; continue; }
                ThrowIfCancelled();
            }

            if (isDir)
            {
                work.Add(new WorkItem(src, dst, true, 0));
                Scan(src, dst, work);
            }
            else
            {
                work.Add(new WorkItem(src, dst, false, size));
            }
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
                Attempt(w.Dst, () => Directory.CreateDirectory(w.Dst));
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
                try { Directory.Delete(work[i].Src, false); } catch { /* 건너뛴 파일이 남은 폴더 */ }
            }
        }
    }

    private unsafe void Scan(string srcRoot, string dstRoot, List<WorkItem> work)
    {
        var stack = new Stack<(string Src, string Dst)>();
        stack.Push((srcRoot, dstRoot));
        while (stack.Count > 0)
        {
            ThrowIfCancelled();
            var (s, d) = stack.Pop();
            CurrentFile = s;
            Touch();

            var entries = new List<(string Name, long Size, uint Attr)>();
            FmEntryBatchCallback cb = (e, n, _) =>
            {
                for (int i = 0; i < n; i++)
                    entries.Add((Marshal.PtrToStringUni(e[i].Name)!, (long)e[i].Size, e[i].Attributes));
                Touch();
                return IsCancelled ? 0 : 1;
            };

            bool ok = Attempt(s, () =>
            {
                entries.Clear();
                int rc = NativeMethods.FmEnumDirectory(s, cb, IntPtr.Zero, CancelPtr, null);
                GC.KeepAlive(cb);
                if (rc != 0) throw Win32Errors.ToIOException(rc, s);
            });
            if (!ok) continue;

            foreach (var (name, size, attr) in entries)
            {
                var sp = Path.Combine(s, name);
                var dp = Path.Combine(d, name);
                if ((attr & (uint)FileAttributes.Directory) != 0)
                {
                    work.Add(new WorkItem(sp, dp, true, 0));
                    if ((attr & (uint)FileAttributes.ReparsePoint) == 0) stack.Push((sp, dp));
                }
                else
                {
                    work.Add(new WorkItem(sp, dp, false, size));
                }
            }
        }
    }

    private unsafe void TransferFile(WorkItem w)
    {
        CurrentFile = w.Src;
        CurrentSize = w.Size;
        CurrentDone = 0;
        Touch();

        try
        {
            bool overwrite = false;
            if (Directory.Exists(w.Dst)) { ReportError(w.Dst, "같은 이름의 폴더가 이미 있습니다."); return; }
            if (File.Exists(w.Dst))
            {
                if (!ResolveConflict(w, out overwrite)) { Skipped++; return; }
            }

            bool rename = Kind == OpKind.Move && PathUtil.SameVolume(w.Src, w.Dst);
            bool ok = Attempt(w.Src, () =>
            {
                CurrentDone = 0;
                DoneBytes = _bytesBase;
                int rc = rename
                    ? NativeMethods.FmMoveFile(w.Src, w.Dst, overwrite ? 1 : 0, null, IntPtr.Zero, CancelPtr)
                    : NativeMethods.FmCopyFile(w.Src, w.Dst, overwrite ? 1 : 0, _progressCb, IntPtr.Zero, CancelPtr);
                if (rc != 0)
                {
                    ThrowIfCancelled();
                    throw Win32Errors.ToIOException(rc, w.Src);
                }
            });
            if (!ok) return;

            if (Kind == OpKind.Move && !rename)
            {
                Attempt(w.Src, () =>
                {
                    var a = File.GetAttributes(w.Src);
                    if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(w.Src, a & ~FileAttributes.ReadOnly);
                    File.Delete(w.Src);
                });
            }
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

    private bool ResolveConflict(WorkItem w, out bool overwrite)
    {
        overwrite = false;
        var src = new FileInfo(w.Src);
        var dst = new FileInfo(w.Dst);
        var choice = _conflictAll ?? AskConflict(new ConflictInfo(w.Src, w.Dst, src.Length, src.LastWriteTime, dst.Length, dst.LastWriteTime));
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
            ConflictChoice.OverwriteOlderAll => src.LastWriteTimeUtc > dst.LastWriteTimeUtc,
            _ => false,
        };
        return overwrite;
    }

    /// <summary>작업 실행. 실패하면 사용자에게 재시도/건너뛰기/중단을 묻는다. 건너뛰면 false.</summary>
    private bool Attempt(string path, Action action)
    {
        while (true)
        {
            try
            {
                action();
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ThrowIfCancelled();
                switch (AskError(path, ex.Message))
                {
                    case ErrorChoice.Retry: Touch(); continue;
                    case ErrorChoice.Skip: Errors++; Touch(); return false;
                    default: Cancel(); throw new OperationCanceledException();
                }
            }
        }
    }

    private void ReportError(string path, string message)
    {
        Errors++;
        if (AskError(path, message) == ErrorChoice.Cancel)
        {
            Cancel();
            throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// 입력이 '\' 로 끝나거나 존재하는 폴더면 그 안으로, 아니면 (단일 항목일 때) 새 이름으로 해석한다.
    /// </summary>
    private (string TargetDir, string? NewName) ResolveDestination()
    {
        var input = Environment.ExpandEnvironmentVariables(_destination.Trim().Trim('"'));
        if (!Path.IsPathRooted(input))
            input = Path.Combine(Path.GetDirectoryName(PathUtil.TrimEnd(_sources[0])) ?? "", input);
        bool endsWithSlash = input.EndsWith('\\') || input.EndsWith('/');
        input = Path.GetFullPath(input);

        if (endsWithSlash || Directory.Exists(input))
            return (PathUtil.TrimEnd(input), null);
        if (_sources.Count == 1)
            return (Path.GetDirectoryName(input)!, Path.GetFileName(input));
        return (input, null);
    }
}
