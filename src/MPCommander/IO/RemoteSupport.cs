using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using MPCommander.Model;

namespace MPCommander.IO;

/// <summary>FTP / WebDAV 공통: 사전 연결 체크, 진행 중 요청 추적(끊기용), 인증서 예외</summary>
internal static class RemoteSupport
{
    /// <summary>진행(응답·데이터)이 이 시간 동안 없으면 끊는다 (먹통 방지 요구: 4~5초)</summary>
    public const int IdleTimeoutMs = 5000;
    /// <summary>요청 자체의 상한 (작업 스레드에서 GuardedIo 없이 쓰일 때의 안전망)</summary>
    public const int RequestTimeoutMs = 8000;
    private const int OkTtlMs = 30_000;
    private const int DownTtlMs = 5_000;

    private sealed record Probe(bool Ok, long At);

    private static readonly ConcurrentDictionary<string, Probe> ProbeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<WebRequest, string> Active = new();
    private static readonly ConcurrentDictionary<string, bool> IgnoreCertHosts = new(StringComparer.OrdinalIgnoreCase);
    private static int _certHooked;

    /// <summary>
    /// 서버 포트로 TCP 연결을 시도해 2초 안에 안 되면 HostUnreachableException (먹통 방지).
    /// 결과는 캐시한다. 작업 스레드에서만 호출.
    /// </summary>
    public static void EnsureReachable(RemoteConnection c)
    {
        var (host, port) = c.Endpoint;
        var key = host + ":" + port;
        if (ProbeCache.TryGetValue(key, out var p) && Compat.TickCount64 - p.At < (p.Ok ? OkTtlMs : DownTtlMs))
        {
            if (!p.Ok) throw new HostUnreachableException($"{host}:{port}");
            return;
        }

        bool ok;
        using (var client = new TcpClient())
        {
            try
            {
                var connect = client.ConnectAsync(host, port);
                ok = connect.Wait(NetworkHealth.ProbeTimeoutMs) && client.Connected;
            }
            catch
            {
                ok = false;
            }
        }
        ProbeCache[key] = new Probe(ok, Compat.TickCount64);
        if (!ok) throw new HostUnreachableException($"{host}:{port}");
    }

    public static void MarkDown(RemoteConnection c)
    {
        var (host, port) = c.Endpoint;
        ProbeCache[host + ":" + port] = new Probe(false, Compat.TickCount64);
    }

    public static void InvalidateProbes() => ProbeCache.Clear();

    /// <summary>요청을 추적하면서 실행 (Abort 로 끊을 수 있게)</summary>
    public static T Track<T>(WebRequest request, string connectionName, Func<T> run)
    {
        Active[request] = connectionName;
        try { return run(); }
        finally { Active.TryRemove(request, out _); }
    }

    /// <summary>연결 이름의 진행 중 요청을 모두 끊는다 (null 이면 전부)</summary>
    public static void AbortAll(string? connectionName)
    {
        foreach (var kv in Active)
        {
            if (connectionName != null && !kv.Value.Equals(connectionName, StringComparison.OrdinalIgnoreCase)) continue;
            try { kv.Key.Abort(); } catch { }
        }
    }

    /// <summary>이 연결의 서버는 인증서 오류를 허용 (사용자가 연결 설정에서 켠 경우만)</summary>
    public static void ApplyCertPolicy(RemoteConnection c)
    {
        var host = c.Endpoint.Host;
        if (c.IgnoreCertErrors) IgnoreCertHosts[host] = true;
        else IgnoreCertHosts.TryRemove(host, out _);

        if (Interlocked.Exchange(ref _certHooked, 1) == 0)
        {
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                var reqHost = (sender as WebRequest)?.RequestUri.Host ?? (sender as string);
                return reqHost != null && IgnoreCertHosts.ContainsKey(reqHost);
            };
        }
    }

    /// <summary>WebException → 사용자에게 보여 줄 IOException</summary>
    public static IOException Translate(WebException ex, string path)
    {
        string detail = ex.Response switch
        {
            FtpWebResponse f => $"{(int)f.StatusCode} {f.StatusDescription?.Trim()}",
            HttpWebResponse h => $"{(int)h.StatusCode} {HttpMessage(h.StatusCode)}",
            _ => ex.Status switch
            {
                WebExceptionStatus.Timeout => "응답 시간 초과",
                WebExceptionStatus.RequestCanceled => "취소됨",
                WebExceptionStatus.NameResolutionFailure => "서버 이름을 찾을 수 없음",
                WebExceptionStatus.ConnectFailure => "서버에 연결할 수 없음",
                WebExceptionStatus.TrustFailure or WebExceptionStatus.SecureChannelFailure =>
                    "보안 연결(인증서) 오류 — 자체 서명 인증서라면 연결 설정에서 '인증서 오류 무시'를 켜세요",
                _ => ex.Message,
            },
        };
        return new IOException($"{detail} ({path})", ex);
    }

    private static string HttpMessage(HttpStatusCode code) => (int)code switch
    {
        401 => "인증 실패 (사용자 이름·비밀번호 확인)",
        403 => "권한 없음",
        404 => "없음",
        405 => "허용되지 않는 작업",
        409 => "상위 폴더가 없음",
        412 => "대상이 이미 있음",
        423 => "잠김",
        507 => "저장 공간 부족",
        _ => code.ToString(),
    };

    public static bool IsNotFound(WebException ex)
        => ex.Response is HttpWebResponse { StatusCode: HttpStatusCode.NotFound } ||
           ex.Response is FtpWebResponse { StatusCode: FtpStatusCode.ActionNotTakenFileUnavailable };

    /// <summary>스트림 복사 + 진행 보고. progress 가 true 를 돌려주면 OperationCanceledException.</summary>
    public static void Pump(Stream input, Stream output, long total, Func<long, long, bool>? progress, Action? onCancel = null)
    {
        var buf = new byte[256 * 1024];
        long done = 0;
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            output.Write(buf, 0, n);
            done += n;
            if (progress != null && progress(done, total))
            {
                onCancel?.Invoke();
                throw new OperationCanceledException();
            }
        }
        progress?.Invoke(done, total);
    }
}
