using System.Runtime.InteropServices;
using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>
/// 진행 창(ProgressWindow)에 표시되는 오래 걸리는 작업의 공통 부분.
/// 전용 스레드에서 Execute 를 돌리고, 취소하면 그 스레드의 막힌 동기 I/O 까지 끊는다.
/// </summary>
public abstract class ProgressOperation
{
    private readonly PinnedInts _pinned = new(1);
    private int[] _cancel => _pinned.Values;
    private readonly object _sync = new();
    private IntPtr _thread;

    // ── 진행 상태 (UI 가 주기적으로 읽음) ──
    public volatile string Phase = "준비 중";
    public volatile string CurrentFile = "";
    public long TotalBytes, DoneBytes, CurrentSize, CurrentDone;
    public int TotalFiles, DoneFiles, Skipped, Errors;
    public long LastProgressTick = Compat.TickCount64;

    public abstract string Title { get; }
    public bool IsCancelled => Volatile.Read(ref _cancel[0]) != 0;

    public Func<ConflictInfo, ConflictChoice> AskConflict { get; set; } = _ => ConflictChoice.Skip;
    public Func<string, string, ErrorChoice> AskError { get; set; } = (_, _) => ErrorChoice.Skip;

    protected unsafe int* CancelPtr => _pinned.Pointer(0);

    public virtual void Cancel()
    {
        Volatile.Write(ref _cancel[0], 1);
        lock (_sync)
        {
            if (_thread != IntPtr.Zero) NativeMethods.FmCancelThreadIo(_thread);
        }
    }

    public Task RunAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            IntPtr h = NativeMethods.FmOpenCurrentThread();
            lock (_sync) _thread = h;
            try { Execute(); tcs.TrySetResult(true); }
            catch (OperationCanceledException) { tcs.TrySetResult(false); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally
            {
                lock (_sync) _thread = IntPtr.Zero;
                NativeMethods.FmCloseHandle(h);
            }
        })
        { IsBackground = true, Name = GetType().Name };
        t.Start();
        return tcs.Task;
    }

    protected abstract void Execute();

    protected void Touch() => Interlocked.Exchange(ref LastProgressTick, Compat.TickCount64);

    protected void ThrowIfCancelled()
    {
        if (IsCancelled) throw new OperationCanceledException();
    }

    /// <summary>작업 실행. 실패하면 재시도/건너뛰기/중단을 묻는다. 건너뛰면 false.</summary>
    protected bool Attempt(string path, Action action)
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

    protected void ReportError(string path, string message)
    {
        Errors++;
        if (AskError(path, message) == ErrorChoice.Cancel)
        {
            Cancel();
            throw new OperationCanceledException();
        }
    }
}
