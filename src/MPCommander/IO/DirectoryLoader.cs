using System.Runtime.InteropServices;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.IO;

public static class DirectoryLoader
{
    /// <summary>네트워크 경로: 이 시간 동안 항목이 하나도 안 들어오면 타임아웃</summary>
    public const int RemoteIdleTimeoutMs = 5000;
    /// <summary>로컬 경로: 이동식/광학 드라이브 회전 대기를 감안</summary>
    public const int LocalIdleTimeoutMs = 10000;

    public static async Task<List<FileItem>> LoadAsync(string path, bool showHidden, IProgress<int>? progress, CancellationToken ct)
    {
        if (PathUtil.IsMtp(path))
        {
            return await GuardedIo.RunAsync(ctx =>
            {
                var items = Mtp.List(path, showHidden, ctx);
                progress?.Report(items.Count);
                return items;
            }, Mtp.IdleTimeoutMs, path, ct, onTimeout: () => Mtp.Abandon(path));
        }

        var host = await NetworkHealth.GetRemoteHostAsync(path);
        if (host != null && !await NetworkHealth.CheckHostAsync(host))
            throw new HostUnreachableException(host);
        ct.ThrowIfCancellationRequested();

        try
        {
            return await GuardedIo.RunAsync(ctx => Enumerate(path, showHidden, ctx, progress),
                host != null ? RemoteIdleTimeoutMs : LocalIdleTimeoutMs, path, ct);
        }
        catch (IoTimeoutException)
        {
            if (host != null) NetworkHealth.MarkDown(host);
            throw;
        }
    }

    private static unsafe List<FileItem> Enumerate(string path, bool showHidden, IoContext ctx, IProgress<int>? progress)
    {
        var list = new List<FileItem>(256);
        FmEntryBatchCallback cb = (entries, count, _) =>
        {
            for (int i = 0; i < count; i++)
            {
                ref FmEntry e = ref entries[i];
                if (!showHidden && (e.Attributes & (uint)FileAttributes.Hidden) != 0) continue;
                list.Add(FileItem.FromNative(path, Marshal.PtrToStringUni(e.Name)!, e.Size, e.LastWrite, e.Attributes));
            }
            progress?.Report(list.Count);
            return ctx.IsCancelled ? 0 : 1;
        };

        int rc = NativeMethods.FmEnumDirectory(path, cb, IntPtr.Zero, ctx.CancelFlag, ctx.ProgressCounter);
        GC.KeepAlive(cb);
        if (rc != 0) throw Win32Errors.ToIOException(rc, path);
        return list;
    }
}
