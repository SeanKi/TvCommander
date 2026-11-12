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
        if (PathUtil.IsVirtual(path))
        {
            // 휴대폰·FTP·WebDAV: 서버/기기 확인과 요청 중단은 각 구현이 맡는다
            var fs = VirtualFs.For(path);
            return await GuardedIo.RunAsync(ctx =>
            {
                var items = VirtualFs.ToFileItems(path, fs.List(path, ctx));
                progress?.Report(items.Count);
                return items;
            }, fs.IdleTimeoutMs, path, ct, onTimeout: () => fs.Abandon(path));
        }

        var host = await NetworkHealth.GetRemoteHostAsync(path);
        if (host != null && !await NetworkHealth.CheckHostAsync(host))
            throw new HostUnreachableException(host);
        ct.ThrowIfCancellationRequested();

        if (PathUtil.IsServerOnly(path))
        {
            // "\\서버" = 공유 폴더 목록 (탐색기처럼). 숨김 공유(이름$)는 숨김 파일 보기일 때만
            return await GuardedIo.RunAsync(_ => ListShares(path, showHidden), RemoteIdleTimeoutMs, path, ct);
        }

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

    // ── 서버의 공유 목록 (NetShareEnum) ──

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        public string shi1_netname;
        public uint shi1_type;
        public string shi1_remark;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int prefMaxLen, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private const uint STYPE_MASK = 0xFF, STYPE_DISKTREE = 0, STYPE_SPECIAL = 0x80000000;

    private static List<FileItem> ListShares(string server, bool showHidden)
    {
        int rc = NetShareEnum(server, 1, out var buf, -1, out int read, out _, IntPtr.Zero);
        if (rc != 0) throw Win32Errors.ToIOException(rc, server);
        var list = new List<FileItem>();
        try
        {
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            for (int i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<SHARE_INFO_1>(buf + i * size);
                if ((info.shi1_type & STYPE_MASK) != STYPE_DISKTREE) continue;            // 프린터·IPC$ 등은 빼고 폴더 공유만
                bool hidden = (info.shi1_type & STYPE_SPECIAL) != 0 || info.shi1_netname.EndsWith("$");   // C$, ADMIN$ ...
                if (hidden && !showHidden) continue;
                list.Add(FileItem.FromNative(server, info.shi1_netname, 0, 0,
                    (uint)(FileAttributes.Directory | (hidden ? FileAttributes.Hidden : 0))));
            }
        }
        finally
        {
            NetApiBufferFree(buf);
        }
        return list;
    }
}
