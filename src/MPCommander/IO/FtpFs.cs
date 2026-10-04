using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MPCommander.Model;

namespace MPCommander.IO;

/// <summary>
/// FTP / FTPS(명시적 TLS) 파일 시스템. 경로: ftp://연결이름/폴더/파일
/// .NET 기본 FtpWebRequest 를 쓴다 (추가 라이브러리 없음). SFTP(SSH) 는 지원하지 않는다.
/// </summary>
internal sealed class FtpFs : IVirtualFs
{
    // drwxr-xr-x  2 user group  4096 Jan  1 12:00 name   /  -rw-r--r-- 1 ftp ftp 123 Mar 05  2023 name
    private static readonly Regex UnixLine = new(
        @"^(?<type>[\-dlbcps])\S{9}\S*\s+(?:\S+\s+){2,3}(?<size>\d+)\s+(?<mon>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<ty>\d{1,2}:\d{2}|\d{4})\s+(?<name>.+)$",
        RegexOptions.CultureInvariant);
    // 01-15-24  03:04PM       <DIR>          name   /   01-15-24  03:04PM            1234 name  (IIS)
    private static readonly Regex WindowsLine = new(
        @"^(?<date>\d{2}-\d{2}-\d{2,4})\s+(?<time>\d{1,2}:\d{2}\s*(?:AM|PM)?)\s+(?:(?<dir><DIR>)|(?<size>\d+))\s+(?<name>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly ConcurrentDictionary<string, VEntry> _cache = new(StringComparer.Ordinal);

    public string Scheme => "ftp";
    public string DisplayName => "FTP";
    public int IdleTimeoutMs => RemoteSupport.IdleTimeoutMs;

    // ───────────────────────── 요청 ─────────────────────────

    private static Uri MakeUri(RemoteConnection c, IEnumerable<string> segments, bool directory)
    {
        var sb = new StringBuilder("ftp://").Append(c.Address).Append(':').Append(c.EffectivePort).Append('/');
        var all = c.StartPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Concat(segments).ToList();
        sb.Append(string.Join("/", all.Select(Uri.EscapeDataString)));
        if (directory && all.Count > 0) sb.Append('/');
        return new Uri(sb.ToString());
    }

    private static FtpWebRequest Request(RemoteConnection c, Uri uri, string method)
    {
        RemoteSupport.ApplyCertPolicy(c);
        var r = (FtpWebRequest)WebRequest.Create(uri);
        r.Method = method;
        r.Credentials = new NetworkCredential(c.User.Length > 0 ? c.User : "anonymous", c.Password);
        r.EnableSsl = c.Kind == RemoteKind.Ftps;
        r.UsePassive = c.Passive;
        r.UseBinary = true;
        r.KeepAlive = true;
        r.Timeout = RemoteSupport.RequestTimeoutMs;
        r.ReadWriteTimeout = RemoteSupport.RequestTimeoutMs;
        r.ConnectionGroupName = "mpc-ftp-" + c.Name;
        r.Proxy = null;
        return r;
    }

    private static (RemoteConnection Conn, string[] Segs) Resolve(string path)
    {
        var c = RemoteConnections.ForPath(path);
        return (c, PathUtil.RemoteSegments(path));
    }

    private static T Run<T>(RemoteConnection c, FtpWebRequest r, string path, Func<T> work)
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

    private static void Simple(RemoteConnection c, Uri uri, string method, string path, Action<FtpWebRequest>? setup = null)
    {
        var r = Request(c, uri, method);
        setup?.Invoke(r);
        Run(c, r, path, () =>
        {
            using var resp = (FtpWebResponse)r.GetResponse();
            return true;
        });
    }

    // ───────────────────────── 목록 ─────────────────────────

    public List<VEntry> List(string path, IoContext? ctx)
    {
        var (c, segs) = Resolve(path);
        RemoteSupport.EnsureReachable(c);
        var r = Request(c, MakeUri(c, segs, directory: true), WebRequestMethods.Ftp.ListDirectoryDetails);

        var entries = Run(c, r, path, () =>
        {
            var list = new List<VEntry>();
            using var resp = (FtpWebResponse)r.GetResponse();
            using var reader = new StreamReader(resp.GetResponseStream()!, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (ctx != null)
                {
                    ctx.Report();
                    if (ctx.IsCancelled) { r.Abort(); throw new OperationCanceledException(); }
                }
                if (ParseLine(line) is { } e && e.Name != "." && e.Name != "..") list.Add(e);
            }
            return list;
        });

        var prefix = PathUtil.WithSlash(PathUtil.TrimEnd(path));
        foreach (var key in _cache.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.IndexOf('/', prefix.Length) < 0)
                _cache.TryRemove(key, out _);
        foreach (var e in entries) _cache[prefix + e.Name] = e;
        return entries;
    }

    internal static VEntry? ParseLine(string line)
    {
        var m = UnixLine.Match(line);
        if (m.Success)
        {
            var name = m.Groups["name"].Value;
            char type = m.Groups["type"].Value[0];
            if (type == 'l')
            {
                int arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0) name = name.Substring(0, arrow);
            }
            long.TryParse(m.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            bool folder = type is 'd' or 'l';   // 링크는 대개 폴더 링크라 폴더로 본다
            return new VEntry(name, folder ? 0 : size, UnixTime(m), folder);
        }

        m = WindowsLine.Match(line);
        if (m.Success)
        {
            bool folder = m.Groups["dir"].Success;
            long.TryParse(m.Groups["size"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
            long time = 0;
            var text = m.Groups["date"].Value + " " + m.Groups["time"].Value.Replace(" ", "");
            string[] formats = ["MM-dd-yy hh:mmtt", "MM-dd-yyyy hh:mmtt", "MM-dd-yy HH:mm", "MM-dd-yyyy HH:mm"];
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                time = dt.ToFileTimeUtc();
            return new VEntry(m.Groups["name"].Value, folder ? 0 : size, time, folder);
        }
        return null;
    }

    private static long UnixTime(Match m)
    {
        try
        {
            int month = DateTime.ParseExact(m.Groups["mon"].Value, "MMM", CultureInfo.InvariantCulture).Month;
            int day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
            var ty = m.Groups["ty"].Value;
            DateTime dt;
            if (ty.Contains(':'))
            {
                // 시각만 있으면 최근 6개월 안 → 올해, 미래가 되면 작년
                var parts = ty.Split(':');
                var now = DateTime.Now;
                dt = new DateTime(now.Year, month, day, int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture), 0, DateTimeKind.Local);
                if (dt > now.AddDays(1)) dt = dt.AddYears(-1);
            }
            else
            {
                dt = new DateTime(int.Parse(ty, CultureInfo.InvariantCulture), month, day, 0, 0, 0, DateTimeKind.Local);
            }
            return dt.ToFileTimeUtc();
        }
        catch
        {
            return 0;
        }
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
            return null;   // 상위 폴더도 없음
        }
        return _cache.TryGetValue(path, out var e) ? e : null;
    }

    // ───────────────────────── 전송 ─────────────────────────

    public void Download(string path, string localPath, bool overwrite, Func<long, long, bool>? progress)
    {
        var (c, segs) = Resolve(path);
        RemoteSupport.EnsureReachable(c);
        if (!overwrite && File.Exists(localPath)) throw new IOException("파일이 이미 있습니다: " + localPath);
        long total = _cache.TryGetValue(PathUtil.TrimEnd(path), out var e) ? e.Size : -1;

        var r = Request(c, MakeUri(c, segs, directory: false), WebRequestMethods.Ftp.DownloadFile);
        try
        {
            Run(c, r, path, () =>
            {
                using var resp = (FtpWebResponse)r.GetResponse();
                using var input = resp.GetResponseStream()!;
                using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);
                RemoteSupport.Pump(input, output, total > 0 ? total : resp.ContentLength, progress, r.Abort);
                return true;
            });
        }
        catch
        {
            try { File.Delete(localPath); } catch { }   // 반쯤 받은 파일
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
        var r = Request(c, MakeUri(c, segs, directory: false), WebRequestMethods.Ftp.UploadFile);
        r.ContentLength = len;
        Run(c, r, remotePath, () =>
        {
            using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 256 * 1024))
            using (var output = r.GetRequestStream())
                RemoteSupport.Pump(input, output, len, progress, r.Abort);
            using var resp = (FtpWebResponse)r.GetResponse();
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

        if (e.IsFolder)
        {
            if (recursive)
                foreach (var child in List(path, null))
                    Delete(PathUtil.Combine(path, child.Name), recursive: true);
            Simple(c, MakeUri(c, segs, directory: false), WebRequestMethods.Ftp.RemoveDirectory, path);
        }
        else
        {
            Simple(c, MakeUri(c, segs, directory: false), WebRequestMethods.Ftp.DeleteFile, path);
        }
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
        Simple(c, MakeUri(c, segs, directory: false), WebRequestMethods.Ftp.MakeDirectory, path);
        _cache[path] = new VEntry(PathUtil.LastSegment(path), 0, DateTime.UtcNow.ToFileTimeUtc(), true);
    }

    public void Rename(string path, string newName)
    {
        path = PathUtil.TrimEnd(path);
        var (c, segs) = Resolve(path);
        RemoteSupport.EnsureReachable(c);
        Simple(c, MakeUri(c, segs, directory: false), WebRequestMethods.Ftp.Rename, path, r => r.RenameTo = newName);
        Forget(PathUtil.Parent(path)! + "/");
    }

    public (ulong Free, ulong Total)? SpaceInfo(string path) => null;   // FTP 에는 표준 용량 조회가 없다

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

    private void Forget(string pathOrPrefix)
    {
        foreach (var key in _cache.Keys)
            if (key.Equals(pathOrPrefix.TrimEnd('/'), StringComparison.Ordinal) || key.StartsWith(pathOrPrefix.TrimEnd('/') + "/", StringComparison.Ordinal))
                _cache.TryRemove(key, out _);
    }
}
