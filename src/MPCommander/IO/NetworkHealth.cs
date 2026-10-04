using System.Collections.Concurrent;
using System.Net.Sockets;
using MPCommander.Native;

namespace MPCommander.IO;

/// <summary>
/// 네트워크 경로 사전 통신 체크. 파일시스템 API 를 호출하기 전에 SMB 포트(445/139)로
/// TCP 연결을 시도해 2초 안에 응답이 없으면 접근하지 않는다. 결과는 서버별로 캐시한다.
/// </summary>
public static class NetworkHealth
{
    public const int ProbeTimeoutMs = 2000;
    private const int OkTtlMs = 30_000;
    private const int DownTtlMs = 5_000;
    private const int DriveMapTtlMs = 60_000;

    private sealed record Entry(bool Ok, long CheckedAt);
    private sealed record DriveMap(string? Host, long CheckedAt);

    private static readonly ConcurrentDictionary<string, Entry> HostCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<bool>> Inflight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, DriveMap> DriveCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>원격 경로면 서버 이름, 로컬이거나 검사 대상이 아니면 null</summary>
    public static async Task<string?> GetRemoteHostAsync(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\"))
            path = path[4..];

        if (path.StartsWith(@"\\"))
        {
            var host = path[2..].Split('\\', 2)[0];
            return IsProbeableHost(host) ? host : null;
        }

        if (path.Length >= 2 && path[1] == ':')
        {
            var drive = char.ToUpperInvariant(path[0]) + ":";
            DriveType type;
            try { type = new DriveInfo(drive).DriveType; }
            catch { return null; }
            if (type != DriveType.Network) return null;
            return await GetHostForDriveAsync(drive).ConfigureAwait(false);
        }
        return null;
    }

    public static async Task<(bool Ok, string? Host)> CheckPathAsync(string path)
    {
        var host = await GetRemoteHostAsync(path).ConfigureAwait(false);
        if (host == null) return (true, null);
        return (await CheckHostAsync(host).ConfigureAwait(false), host);
    }

    public static async Task<bool> CheckHostAsync(string host)
    {
        if (HostCache.TryGetValue(host, out var e))
        {
            long age = Compat.TickCount64 - e.CheckedAt;
            if (age < (e.Ok ? OkTtlMs : DownTtlMs)) return e.Ok;
        }

        var task = Inflight.GetOrAdd(host, ProbeAsync);
        try
        {
            bool ok = await task.ConfigureAwait(false);
            HostCache[host] = new Entry(ok, Compat.TickCount64);
            return ok;
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Task<bool>>>)Inflight).Remove(new KeyValuePair<string, Task<bool>>(host, task));
        }
    }

    /// <summary>열거 타임아웃 등으로 서버가 죽은 것이 확인되면 호출 → 이후 접근은 즉시 실패</summary>
    public static void MarkDown(string host) => HostCache[host] = new Entry(false, Compat.TickCount64);

    public static void InvalidateAll()
    {
        HostCache.Clear();
        DriveCache.Clear();
    }

    private static bool IsProbeableHost(string host)
    {
        if (Compat.IsEmpty(host) || host.Contains('@')) return false;           // WebDAV
        return !host.Equals("wsl$", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("wsl.localhost", StringComparison.OrdinalIgnoreCase)
            && !host.Equals("tsclient", StringComparison.OrdinalIgnoreCase);           // RDP 리디렉션
    }

    private static async Task<string?> GetHostForDriveAsync(string drive)
    {
        if (DriveCache.TryGetValue(drive, out var m) && Compat.TickCount64 - m.CheckedAt < DriveMapTtlMs)
            return m.Host;

        string? host = null;
        try
        {
            var unc = await GuardedIo.RunAsync(_ =>
            {
                unsafe
                {
                    char* buf = stackalloc char[1024];
                    int rc = NativeMethods.FmGetUncForDrive(drive, buf, 1024);
                    return rc == 0 ? new string(buf) : null;
                }
            }, ProbeTimeoutMs, drive).ConfigureAwait(false);

            if (unc != null && unc.StartsWith(@"\\"))
            {
                var h = unc[2..].Split('\\', 2)[0];
                host = IsProbeableHost(h) ? h : null;
            }
        }
        catch { /* 매핑 정보를 못 얻으면 사전 체크 생략, 열거 워치독에 맡긴다 */ }

        DriveCache[drive] = new DriveMap(host, Compat.TickCount64);
        return host;
    }

    private static async Task<bool> ProbeAsync(string host)
    {
        using var cts = new CancellationTokenSource(ProbeTimeoutMs);
        var pending = new List<Task<bool>> { ConnectAsync(host, 445, cts.Token), ConnectAsync(host, 139, cts.Token) };
        var deadline = Task.Delay(ProbeTimeoutMs + 300);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending.Cast<Task>().Append(deadline)).ConfigureAwait(false);
            if (done == deadline) return false;
            var t = (Task<bool>)done;
            if (t.Result) { cts.Cancel(); return true; }
            pending.Remove(t);
        }
        return false;
    }

    private static async Task<bool> ConnectAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            await Compat.ConnectAsync(client, host, port, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
