using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using MPCommander.Model;

namespace MPCommander.IO;

/// <summary>
/// WebDAV 파일 시스템. 경로: dav://연결이름/폴더/파일 → 연결 URL 아래 경로.
/// Windows 의 WebDAV 드라이브 연결(WebClient 서비스)을 거치지 않고 직접 HTTP 로 통신한다.
/// 서버가 알려 주면 실제 여유 공간(quota)도 표시한다.
/// </summary>
internal sealed class WebDavFs : IVirtualFs
{
    private static readonly XNamespace D = "DAV:";
    private const string ListBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>";
    private const string QuotaBody =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop>" +
        "<d:quota-available-bytes/><d:quota-used-bytes/></d:prop></d:propfind>";

    private readonly ConcurrentDictionary<string, VEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string Scheme => "dav";
    public string DisplayName => "WebDAV";
    public int IdleTimeoutMs => RemoteSupport.IdleTimeoutMs;

    // ───────────────────────── 요청 ─────────────────────────

    private static Uri MakeUri(RemoteConnection c, IEnumerable<string> segments, bool directory)
    {
        var baseUrl = c.Address.TrimEnd('/');
        var segs = segments.ToList();
        var url = baseUrl + (segs.Count > 0 ? "/" + string.Join("/", segs.Select(Uri.EscapeDataString)) : "");
        if (directory) url += "/";
        return new Uri(url);
    }

    private static HttpWebRequest Request(RemoteConnection c, Uri uri, string method)
    {
        RemoteSupport.ApplyCertPolicy(c);
        var r = (HttpWebRequest)WebRequest.Create(uri);
        r.Method = method;
        r.UserAgent = "MP-Commander";
        r.Timeout = RemoteSupport.RequestTimeoutMs;
        r.ReadWriteTimeout = RemoteSupport.RequestTimeoutMs;
        r.KeepAlive = true;
        r.ConnectionGroupName = "mpc-dav-" + c.Name;
        if (c.User.Length > 0)
        {
            // 대부분의 WebDAV 서비스는 Basic 인증 → 미리 보내 업로드(PUT) 때 401 재시도를 피한다. Digest 등은 Credentials 로.
            r.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(c.User + ":" + c.Password));
            r.Credentials = new NetworkCredential(c.User, c.Password);
        }
        return r;
    }

    private static (RemoteConnection Conn, string[] Segs) Resolve(string path)
        => (RemoteConnections.ForPath(path), PathUtil.RemoteSegments(path));

    private static T Run<T>(RemoteConnection c, HttpWebRequest r, string path, Func<T> work)
    {
        try
        {
            return RemoteSupport.Track(r, c.Name, work);
        }
        catch (WebException ex) when (ex.Status == WebExceptionStatus.RequestCanceled)
        {
            throw new OperationCanceledException();
        }
        catch (WebException ex)
        {
            throw RemoteSupport.Translate(ex, path);
        }
    }

    private static XDocument PropFind(RemoteConnection c, Uri uri, string depth, string body, string path)
    {
        var r = Request(c, uri, "PROPFIND");
        r.Headers["Depth"] = depth;
        r.ContentType = "application/xml; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(body);
        r.ContentLength = bytes.Length;
        return Run(c, r, path, () =>
        {
            using (var s = r.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using var resp = (HttpWebResponse)r.GetResponse();
            using var stream = resp.GetResponseStream()!;
            return XDocument.Load(stream);
        });
    }

    private static void Simple(RemoteConnection c, Uri uri, string method, string path, Action<HttpWebRequest>? setup = null)
    {
        var r = Request(c, uri, method);
        setup?.Invoke(r);
        Run(c, r, path, () =>
        {
            using var resp = (HttpWebResponse)r.GetResponse();
            return true;
        });
    }

    // ───────────────────────── 목록 ─────────────────────────

    public List<VEntry> List(string path, IoContext? ctx)
    {
        var (c, segs) = Resolve(path);
        RemoteSupport.EnsureReachable(c);
        var uri = MakeUri(c, segs, directory: true);
        var doc = PropFind(c, uri, "1", ListBody, path);
        var self = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');

        var list = new List<VEntry>();
        foreach (var resp in doc.Descendants(D + "response"))
        {
            ctx?.Report();
            if (ctx?.IsCancelled == true) throw new OperationCanceledException();

            var href = resp.Element(D + "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;
            var abs = Uri.UnescapeDataString(new Uri(uri, href).AbsolutePath).TrimEnd('/');
            if (abs.Equals(self, StringComparison.OrdinalIgnoreCase) || abs.Length == 0) continue;   // 자기 자신
            var name = abs.Substring(abs.LastIndexOf('/') + 1);

            // 200 OK 인 propstat 의 prop 만 본다
            var prop = resp.Elements(D + "propstat")
                .Where(ps => (ps.Element(D + "status")?.Value ?? "").Contains(" 200"))
                .Select(ps => ps.Element(D + "prop"))
                .FirstOrDefault(p => p != null);

            bool folder = prop?.Element(D + "resourcetype")?.Element(D + "collection") != null;
            long.TryParse(prop?.Element(D + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            long time = 0;
            if (DateTime.TryParse(prop?.Element(D + "getlastmodified")?.Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                time = dt.ToFileTimeUtc();
            list.Add(new VEntry(name, folder ? 0 : size, time, folder));
        }

        var prefix = PathUtil.WithSlash(PathUtil.TrimEnd(path));
        foreach (var key in _cache.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.IndexOf('/', prefix.Length) < 0)
                _cache.TryRemove(key, out _);
        foreach (var e in list) _cache[prefix + e.Name] = e;
        return list;
    }

    public VEntry? GetEntry(string path)
    {
        path = PathUtil.TrimEnd(path);
        var parent = PathUtil.Parent(path);
        if (parent == null) return new VEntry(PathUtil.RootName(path), 0, 0, true);
        if (_cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            List(parent, null);
        }
        catch (IOException ex) when (ex.InnerException is WebException we && RemoteSupport.IsNotFound(we))
        {
            return null;
        }
        return _cache.TryGetValue(path, out var e) ? e : null;
    }

    // ───────────────────────── 전송 ─────────────────────────

    public void Download(string path, string localPath, bool overwrite, Func<long, long, bool>? progress)
    {
        var (c, segs) = Resolve(path);
        RemoteSupport.EnsureReachable(c);
        if (!overwrite && File.Exists(localPath)) throw new IOException("파일이 이미 있습니다: " + localPath);

        var r = Request(c, MakeUri(c, segs, directory: false), "GET");
        try
        {
            Run(c, r, path, () =>
            {
                using var resp = (HttpWebResponse)r.GetResponse();
                using var input = resp.GetResponseStream()!;
                using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);
                RemoteSupport.Pump(input, output, resp.ContentLength, progress, r.Abort);
                return true;
            });
        }
        catch
        {
            try { File.Delete(localPath); } catch { }
            throw;
        }
    }

    public void Upload(string localPath, string remotePath, bool overwrite, Func<long, long, bool>? progress)
    {
        remotePath = PathUtil.TrimEnd(remotePath);
        var parent = PathUtil.Parent(remotePath) ?? throw new IOException("연결 루트 경로가 잘못되었습니다.");
        var (c, segs) = Resolve(remotePath);
        RemoteSupport.EnsureReachable(c);
        if (PathUtil.Parent(parent) != null) EnsureFolder(parent);
        if (!overwrite && GetEntry(remotePath) != null) throw new IOException("파일이 이미 있습니다: " + remotePath);

        var len = new FileInfo(localPath).Length;
        var r = Request(c, MakeUri(c, segs, directory: false), "PUT");
        r.ContentLength = len;
        r.AllowWriteStreamBuffering = false;   // 큰 파일도 메모리에 올리지 않고 바로 보낸다
        r.ContentType = "application/octet-stream";
        Run(c, r, remotePath, () =>
        {
            using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024))
            using (var output = r.GetRequestStream())
                RemoteSupport.Pump(input, output, len, progress, r.Abort);
            using var resp = (HttpWebResponse)r.GetResponse();
            return true;
        });
        _cache[remotePath] = new VEntry(PathUtil.LastSegment(remotePath), len, DateTime.UtcNow.ToFileTimeUtc(), false);
    }

    // ───────────────────────── 폴더 / 삭제 / 이름 ─────────────────────────

    public void Delete(string path, bool recursive)
    {
        path = PathUtil.TrimEnd(path);
        if (PathUtil.Parent(path) == null) throw new IOException("연결 루트는 삭제할 수 없습니다.");
        var (c, segs) = Resolve(path);
        RemoteSupport.EnsureReachable(c);
        var e = GetEntry(path) ?? throw new FileNotFoundException("없는 경로입니다: " + path);
        if (e.IsFolder && !recursive && List(path, null).Count > 0) throw new IOException("폴더가 비어 있지 않습니다: " + path);
        Simple(c, MakeUri(c, segs, directory: e.IsFolder), "DELETE", path);   // WebDAV 의 폴더 삭제는 원래 하위까지 지운다
        Forget(path);
    }

    public void EnsureFolder(string path)
    {
        path = PathUtil.TrimEnd(path);
        var parent = PathUtil.Parent(path);
        if (parent == null) return;
        var e = GetEntry(path);
        if (e != null)
        {
            if (!e.IsFolder) throw new IOException("같은 이름의 파일이 있습니다: " + path);
            return;
        }
        EnsureFolder(parent);
        var (c, segs) = Resolve(path);
        Simple(c, MakeUri(c, segs, directory: true), "MKCOL", path);
        _cache[path] = new VEntry(PathUtil.LastSegment(path), 0, DateTime.UtcNow.ToFileTimeUtc(), true);
    }

    public void Rename(string path, string newName)
    {
        path = PathUtil.TrimEnd(path);
        var (c, segs) = Resolve(path);
        RemoteSupport.EnsureReachable(c);
        var e = GetEntry(path) ?? throw new FileNotFoundException("없는 경로입니다: " + path);
        var target = segs.Take(segs.Length - 1).Concat([newName]);
        var dest = MakeUri(c, target, directory: e.IsFolder);
        Simple(c, MakeUri(c, segs, directory: e.IsFolder), "MOVE", path, r =>
        {
            r.Headers["Destination"] = dest.AbsoluteUri;
            r.Headers["Overwrite"] = "F";
        });
        Forget(PathUtil.Parent(path)!);
    }

    /// <summary>서버가 RFC 4331 quota 속성을 주면 실제 용량, 아니면 null</summary>
    public (ulong Free, ulong Total)? SpaceInfo(string path)
    {
        var c = RemoteConnections.ForPath(path);
        RemoteSupport.EnsureReachable(c);
        try
        {
            var doc = PropFind(c, MakeUri(c, [], directory: true), "0", QuotaBody, path);
            var avail = doc.Descendants(D + "quota-available-bytes").Select(x => x.Value).FirstOrDefault();
            var used = doc.Descendants(D + "quota-used-bytes").Select(x => x.Value).FirstOrDefault();
            if (!ulong.TryParse(avail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var free)) return null;
            ulong.TryParse(used, NumberStyles.Integer, CultureInfo.InvariantCulture, out var usedBytes);
            return (free, free + usedBytes);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Abandon(string path)
    {
        try
        {
            var c = RemoteConnections.ForPath(path);
            RemoteSupport.AbortAll(c.Name);
            RemoteSupport.MarkDown(c);
        }
        catch { }
    }

    public void CancelAll() => RemoteSupport.AbortAll(null);

    private void Forget(string path)
    {
        var p = path.TrimEnd('/');
        foreach (var key in _cache.Keys)
            if (key.Equals(p, StringComparison.OrdinalIgnoreCase) || key.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
                _cache.TryRemove(key, out _);
    }
}
