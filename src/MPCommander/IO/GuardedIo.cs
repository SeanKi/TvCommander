using System.ComponentModel;
using System.Runtime.InteropServices;
using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>응답이 없어 중단된 I/O</summary>
public sealed class IoTimeoutException : IOException
{
    public IoTimeoutException(string? target, int timeoutMs)
        : base($"응답 시간 초과 ({timeoutMs / 1000.0:0.#}초): {target}") { }
}

/// <summary>사전 통신 체크에서 서버가 응답하지 않음</summary>
public sealed class HostUnreachableException : IOException
{
    public HostUnreachableException(string host)
        : base($"'{host}' 서버에 연결할 수 없습니다. ({NetworkHealth.ProbeTimeoutMs / 1000}초 안에 응답 없음)") => Host = host;
    public string Host { get; }
}

internal static class Win32Errors
{
    public const int OperationAborted = 995;
    public const int RequestAborted = 1235;
    public const int Cancelled = 1223;

    public static string Message(int code) => new Win32Exception(code).Message;

    public static IOException ToIOException(int code, string path)
        => new($"{Message(code)} ({path})", unchecked((int)(0x80070000u | (uint)code)));
}

/// <summary>
/// 네이티브 코드와 공유하는 취소 플래그와 진행 카운터.
/// 고정(pinned) 배열이라 작업 스레드가 버려진 뒤에도 안전하게 쓸 수 있다.
/// </summary>
public sealed class IoContext
{
    private readonly PinnedInts _pinned = new(2);
    private int[] _flags => _pinned.Values;

    internal unsafe int* CancelFlag => _pinned.Pointer(0);
    internal unsafe int* ProgressCounter => _pinned.Pointer(1);

    public bool IsCancelled => Volatile.Read(ref _flags[0]) != 0;
    public void Report() => Interlocked.Increment(ref _flags[1]);

    internal int Progress => Volatile.Read(ref _flags[1]);
    internal void Cancel() => Volatile.Write(ref _flags[0], 1);
}

/// <summary>
/// 먹통 방지 실행기. 작업을 전용 스레드에서 돌리고, 진행이 idleTimeout 동안 없으면
/// 결과를 타임아웃으로 확정한 뒤 CancelSynchronousIo 로 막힌 I/O 를 깨운다.
/// 그래도 깨어나지 않는 스레드는 버린다(백그라운드 스레드라 종료를 막지 않음).
/// </summary>
public static class GuardedIo
{
    /// <param name="onTimeout">타임아웃/취소 시 추가로 할 일 (예: MTP 기기 작업 취소)</param>
    public static Task<T> RunAsync<T>(Func<IoContext, T> work, int idleTimeoutMs, string? target,
                                      CancellationToken ct = default, bool sta = false, Action? onTimeout = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new IoContext();
        var sync = new object();
        IntPtr handle = IntPtr.Zero;
        bool finished = false;

        var thread = new Thread(() =>
        {
            IntPtr h = NativeMethods.FmOpenCurrentThread();
            lock (sync) handle = h;
            try { tcs.TrySetResult(work(ctx)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally
            {
                lock (sync) { finished = true; handle = IntPtr.Zero; }
                NativeMethods.FmCloseHandle(h);
            }
        })
        { IsBackground = true, Name = "GuardedIo" };
        if (sta) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        _ = WatchAsync();
        return tcs.Task;

        async Task WatchAsync()
        {
            int last = ctx.Progress;
            long lastChange = Compat.TickCount64;
            while (!tcs.Task.IsCompleted)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (tcs.Task.IsCompleted) return;

                long now = Compat.TickCount64;
                int p = ctx.Progress;
                if (p != last) { last = p; lastChange = now; continue; }

                bool userCancel = ct.IsCancellationRequested;
                if (!userCancel && now - lastChange < idleTimeoutMs) continue;

                // 먼저 막힌 작업을 끊고(다음 시도가 이 정리와 겹치지 않게) 결과를 확정한다
                ctx.Cancel();
                lock (sync)
                {
                    if (!finished && handle != IntPtr.Zero)
                        NativeMethods.FmCancelThreadIo(handle);
                }
                try { onTimeout?.Invoke(); } catch { }
                tcs.TrySetException(userCancel ? new OperationCanceledException(ct) : new IoTimeoutException(target, idleTimeoutMs));
                return;
            }
        }
    }

    /// <summary>타임아웃 없이 전용 스레드에서 실행 (셸 삭제처럼 오래 걸리고 자체 UI 가 있는 작업용)</summary>
    public static Task<T> RunLongAsync<T>(Func<T> work, bool sta = false)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        { IsBackground = true, Name = "LongIo" };
        if (sta) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
