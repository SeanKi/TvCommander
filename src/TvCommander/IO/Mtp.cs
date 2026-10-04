using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TvCommander.Model;
using TvCommander.Native;

namespace TvCommander.IO;

public sealed record MtpDeviceInfo(string Id, string Name);

public sealed record MtpEntry(string ObjectId, string Name, long Size, long LastWrite, bool IsFolder);

/// <summary>
/// 안드로이드폰 등 MTP 기기. 경로는 "mtp://기기이름/저장소/폴더/파일" 형식이고,
/// 경로 → WPD objectId 는 목록을 읽을 때 캐시해 둔다.
/// 여기 메서드는 모두 동기 호출이다. UI 에서는 반드시 GuardedIo 등 작업 스레드에서 부른다.
/// </summary>
public static class Mtp
{
    public const string Prefix = "mtp://";
    /// <summary>MTP 는 느리다. 이 시간 동안 진행이 없으면 타임아웃.</summary>
    public const int IdleTimeoutMs = 10000;

    private const string RootId = "DEVICE";
    private static readonly int CancelledHr = unchecked((int)0x800704C7);   // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    private sealed class Session
    {
        public required string Id;
        public required string Name;
        public IntPtr Handle;
        public readonly ConcurrentDictionary<string, MtpEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private static List<MtpDeviceInfo> _devices = new();
    private static long _devicesTick;

    // ───────────────────────── 기기 ─────────────────────────

    public static IReadOnlyList<MtpDeviceInfo> ListDevices()
    {
        var raw = new List<(string Id, string Name)>();
        FmMtpDeviceCallback cb = (id, name, _) => { raw.Add((id, name)); return 1; };
        int hr = NativeMethods.FmMtpListDevices(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        if (hr < 0) throw Error(hr, "MTP");

        // 경로에 쓰므로 '/' '\' 제거, 같은 이름은 (2) (3)...
        var list = new List<MtpDeviceInfo>();
        foreach (var (id, name) in raw.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            // USB 메모리/외장 디스크도 WPD 로 보이지만(wpdbusenum) 이미 드라이브 문자가 있으므로 뺀다
            if (id.Contains("wpdbusenum", StringComparison.OrdinalIgnoreCase)) continue;
            var clean = name.Replace('/', '_').Replace('\\', '_').Trim();
            var unique = clean;
            for (int n = 2; list.Any(d => d.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); n++)
                unique = $"{clean} ({n})";
            list.Add(new MtpDeviceInfo(id, unique));
        }
        lock (Sync)
        {
            _devices = list;
            _devicesTick = Environment.TickCount64;
        }
        return list;
    }

    /// <summary>드라이브 목록용. 3초 캐시, 4초 타임아웃.</summary>
    public static async Task<IReadOnlyList<MtpDeviceInfo>> ListDevicesAsync()
    {
        lock (Sync)
        {
            if (Environment.TickCount64 - _devicesTick < 3000) return _devices;
        }
        try { return await GuardedIo.RunAsync(_ => ListDevices(), 4000, "MTP 기기 목록"); }
        catch { return Array.Empty<MtpDeviceInfo>(); }
    }

    private static (string Device, string[] Segments) Split(string path)
    {
        var rest = path[Prefix.Length..].Trim('/');
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new IOException("기기 이름이 없는 경로입니다: " + path);
        return (parts[0], parts[1..]);
    }

    private static Session GetSession(string path)
    {
        var (deviceName, _) = Split(path);
        MtpDeviceInfo? Find()
        {
            lock (Sync)
                return _devices.FirstOrDefault(d => d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        }

        lock (Sync)
        {
            if (Sessions.TryGetValue(deviceName, out var existing) && existing.Handle != IntPtr.Zero) return existing;
        }
        var info = Find();
        if (info == null)
        {
            ListDevices();
            info = Find();
        }
        if (info == null) throw new IOException($"'{deviceName}' 기기를 찾을 수 없습니다. USB 연결을 확인하세요.");

        lock (Sync)
        {
            if (Sessions.TryGetValue(deviceName, out var s) && s.Handle != IntPtr.Zero) return s;
            IntPtr h = NativeMethods.FmMtpOpen(info.Id, out int hr);
            if (h == IntPtr.Zero) throw Error(hr, deviceName);
            s = new Session { Id = info.Id, Name = deviceName, Handle = h };
            Sessions[deviceName] = s;
            return s;
        }
    }

    /// <summary>응답 없는 기기: 진행 중인 작업을 취소하고 세션을 버린다 (다음 접근 때 다시 연다).</summary>
    public static void Abandon(string path)
    {
        try
        {
            var (deviceName, _) = Split(path);
            lock (Sync)
            {
                if (!Sessions.Remove(deviceName, out var s)) return;
                if (s.Handle != IntPtr.Zero) NativeMethods.FmMtpCancel(s.Handle);
                // 다른 스레드가 아직 핸들을 쓰고 있을 수 있어 닫지 않는다 (작은 누수 감수)
            }
        }
        catch { }
    }

    /// <summary>모든 기기의 진행 중 작업 취소 (복사 취소 버튼)</summary>
    public static void CancelAll()
    {
        lock (Sync)
            foreach (var s in Sessions.Values)
                if (s.Handle != IntPtr.Zero) NativeMethods.FmMtpCancel(s.Handle);
    }

    // ───────────────────────── 목록 / 경로 해석 ─────────────────────────

    public static unsafe List<MtpEntry> ListChildren(string path) => ListChildren(path, null, null);

    /// <summary>폴더의 자식 목록을 읽고 캐시를 갱신한다.</summary>
    public static unsafe List<MtpEntry> ListChildren(string path, int* cancelFlag = null, int* progress = null)
    {
        path = PathUtil.TrimEnd(path);
        var s = GetSession(path);
        var parentId = ResolveId(s, path);

        var list = new List<MtpEntry>();
        FmMtpEntryCallback cb = (e, n, _) =>
        {
            for (int i = 0; i < n; i++)
            {
                list.Add(new MtpEntry(Marshal.PtrToStringUni(e[i].ObjectId)!, Marshal.PtrToStringUni(e[i].Name)!,
                    (long)e[i].Size, (long)e[i].LastWrite, e[i].IsFolder != 0));
            }
            return cancelFlag != null && *cancelFlag != 0 ? 0 : 1;
        };
        int hr = NativeMethods.FmMtpEnumChildren(s.Handle, parentId, cb, IntPtr.Zero, cancelFlag, progress);
        GC.KeepAlive(cb);
        if (hr == CancelledHr) throw new OperationCanceledException();
        if (hr < 0)
        {
            if (parentId == RootId) Abandon(path);   // 루트도 못 읽으면 연결이 끊긴 것
            throw Error(hr, path);
        }

        // 캐시 갱신: 이 폴더의 기존 자식 항목을 지우고 새로 넣는다
        var prefix = PathUtil.WithSlash(path);
        foreach (var key in s.Entries.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.IndexOf('/', prefix.Length) < 0)
                s.Entries.TryRemove(key, out _);
        foreach (var e in list)
            s.Entries.TryAdd(prefix + e.Name, e);

        if (parentId == RootId && list.Count == 0)
            throw new IOException("저장소가 보이지 않습니다. 폰의 잠금을 풀고, USB 연결 알림에서 '파일 전송'을 선택하세요.");
        return list;
    }

    /// <summary>패널 목록용</summary>
    public static unsafe List<FileItem> List(string path, bool showHidden, IoContext ctx)
    {
        var entries = ListChildren(path, ctx.CancelFlag, ctx.ProgressCounter);
        var dir = PathUtil.TrimEnd(path);
        var items = new List<FileItem>(entries.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!seen.Add(e.Name)) continue;   // MTP 는 같은 이름이 둘 있을 수 있다 (숨김 속성은 없음)
            var attr = e.IsFolder ? FileAttributes.Directory : FileAttributes.Normal;
            items.Add(FileItem.FromNative(dir, e.Name, (ulong)e.Size, (ulong)e.LastWrite, (uint)attr));
        }
        return items;
    }

    /// <summary>경로의 항목 정보. 없으면 null. 기기 루트는 가상 폴더 항목.</summary>
    public static unsafe MtpEntry? GetEntry(string path, bool refresh = false)
    {
        path = PathUtil.TrimEnd(path);
        var s = GetSession(path);
        var parent = PathUtil.Parent(path);
        if (parent == null) return new MtpEntry(RootId, s.Name, 0, 0, true);
        if (!refresh && s.Entries.TryGetValue(path, out var cached)) return cached;
        ListChildren(parent);
        return s.Entries.TryGetValue(path, out var e) ? e : null;
    }

    private static string ResolveId(Session s, string path)
    {
        if (PathUtil.Parent(path) == null) return RootId;
        if (s.Entries.TryGetValue(path, out var e)) return e.ObjectId;
        return (GetEntry(path) ?? throw new DirectoryNotFoundException("휴대폰에 없는 경로입니다: " + path)).ObjectId;
    }

    public static bool IsFolder(string path)
    {
        try { return GetEntry(path)?.IsFolder == true; }
        catch (IOException) { return false; }
    }

    // ───────────────────────── 전송 ─────────────────────────

    internal static unsafe void Download(string path, string localPath, bool overwrite, FmProgressCallback? cb, int* cancelFlag)
    {
        path = PathUtil.TrimEnd(path);
        var s = GetSession(path);
        var id = ResolveId(s, path);
        int hr = NativeMethods.FmMtpDownload(s.Handle, id, localPath, overwrite ? 1 : 0, cb, IntPtr.Zero, cancelFlag);
        GC.KeepAlive(cb);
        if (hr == CancelledHr) throw new OperationCanceledException();
        if (hr < 0) throw Error(hr, path);
    }

    /// <summary>로컬 파일을 휴대폰 경로(dstPath, 파일 이름 포함)로 올린다. 상위 폴더는 없으면 만든다.</summary>
    internal static unsafe void Upload(string localPath, string dstPath, FmProgressCallback? cb, int* cancelFlag)
    {
        dstPath = PathUtil.TrimEnd(dstPath);
        var parent = PathUtil.Parent(dstPath) ?? throw new IOException("기기 루트에는 파일을 둘 수 없습니다. 저장소 안으로 복사하세요.");
        var s = GetSession(dstPath);
        var parentId = EnsureFolder(parent);
        var name = PathUtil.LastSegment(dstPath);

        char* buf = stackalloc char[1024];
        buf[0] = '\0';
        int hr = NativeMethods.FmMtpUpload(s.Handle, parentId, localPath, name, cb, IntPtr.Zero, cancelFlag, buf, 1024);
        GC.KeepAlive(cb);
        if (hr == CancelledHr) throw new OperationCanceledException();
        if (hr < 0) throw Error(hr, dstPath);

        var newId = new string(buf);
        if (newId.Length > 0)
        {
            var fi = new FileInfo(localPath);
            s.Entries[dstPath] = new MtpEntry(newId, name, fi.Length, DateTime.UtcNow.ToFileTimeUtc(), false);
        }
        else
        {
            InvalidateChildren(s, parent);
        }
    }

    /// <summary>폴더가 있으면 그 objectId, 없으면 (상위부터) 만든다.</summary>
    public static unsafe string EnsureFolder(string path)
    {
        path = PathUtil.TrimEnd(path);
        var s = GetSession(path);
        var parent = PathUtil.Parent(path);
        if (parent == null) return RootId;

        var e = GetEntry(path);
        if (e != null)
        {
            if (!e.IsFolder) throw new IOException("같은 이름의 파일이 있습니다: " + path);
            return e.ObjectId;
        }
        if (PathUtil.Parent(parent) == null)
            throw new IOException("기기 루트에는 폴더를 만들 수 없습니다. 저장소 안에 만드세요.");

        var parentId = EnsureFolder(parent);
        var name = PathUtil.LastSegment(path);
        char* buf = stackalloc char[1024];
        buf[0] = '\0';
        int hr = NativeMethods.FmMtpCreateFolder(s.Handle, parentId, name, buf, 1024);
        if (hr < 0) throw Error(hr, path);
        var id = new string(buf);
        s.Entries[path] = new MtpEntry(id, name, 0, DateTime.UtcNow.ToFileTimeUtc(), true);
        return id;
    }

    public static void Delete(string path, bool recursive)
    {
        path = PathUtil.TrimEnd(path);
        var s = GetSession(path);
        if (PathUtil.Parent(path) == null || PathUtil.Parent(PathUtil.Parent(path)!) == null)
            throw new IOException("기기나 저장소 자체는 삭제할 수 없습니다.");
        var id = ResolveId(s, path);
        int hr = NativeMethods.FmMtpDelete(s.Handle, id, recursive ? 1 : 0);
        if (hr < 0) throw Error(hr, path);

        var prefix = PathUtil.WithSlash(path);
        foreach (var key in s.Entries.Keys)
            if (key.Equals(path, StringComparison.OrdinalIgnoreCase) || key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s.Entries.TryRemove(key, out _);
    }

    public static void Rename(string path, string newName)
    {
        path = PathUtil.TrimEnd(path);
        var s = GetSession(path);
        var id = ResolveId(s, path);
        int hr = NativeMethods.FmMtpRename(s.Handle, id, newName);
        if (hr < 0) throw Error(hr, path, "이 기기는 이름 바꾸기를 지원하지 않을 수 있습니다.");
        InvalidateChildren(s, PathUtil.Parent(path)!);
    }

    /// <summary>저장소 여유/전체 용량. 기기 루트면 null.</summary>
    public static (ulong Free, ulong Total)? StorageInfo(string path)
    {
        path = PathUtil.TrimEnd(path);
        var (device, segments) = Split(path);
        if (segments.Length == 0) return null;
        var s = GetSession(path);
        var storageId = ResolveId(s, Prefix + device + "/" + segments[0]);
        int hr = NativeMethods.FmMtpGetStorageInfo(s.Handle, storageId, out var free, out var total);
        return hr < 0 || total == 0 ? null : (free, total);
    }

    private static void InvalidateChildren(Session s, string parent)
    {
        var prefix = PathUtil.WithSlash(parent);
        foreach (var key in s.Entries.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                s.Entries.TryRemove(key, out _);
    }

    private static IOException Error(int hr, string path, string? hint = null)
    {
        var msg = Marshal.GetExceptionForHR(hr)?.Message ?? $"0x{hr:X8}";
        if (msg.StartsWith("Exception from HRESULT", StringComparison.Ordinal) || msg.StartsWith("HRESULT", StringComparison.Ordinal))
            msg = $"휴대폰 작업 실패 (0x{hr:X8})";
        if (hint != null) msg += " " + hint;
        return new IOException($"{msg} ({path})", hr);
    }
}
