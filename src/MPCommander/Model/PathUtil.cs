using System.Text.RegularExpressions;

namespace MPCommander.Model;

/// <summary>
/// 경로 도우미. 일반 Windows 경로와 원격 경로를 함께 다룬다.
/// 원격 경로는 "scheme://이름/폴더/파일" 형식이고 구분자는 '/':
///   mtp://기기/저장소/...   (안드로이드폰 등)
///   ftp://연결이름/...      (FTP / FTPS)
///   dav://연결이름/...      (WebDAV)
/// "scheme://이름" 까지가 루트다.
/// </summary>
public static class PathUtil
{
    public const string MtpPrefix = "mtp://";
    private static readonly Regex VirtualRegex = new(@"^(mtp|ftp|dav)://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>원격(휴대폰·FTP·WebDAV) 경로인가</summary>
    public static bool IsVirtual(string? p) => p != null && VirtualRegex.IsMatch(p);

    /// <summary>"ftp://..." → "ftp"</summary>
    public static string Scheme(string p) => p.Substring(0, p.IndexOf("://", StringComparison.Ordinal)).ToLowerInvariant();

    private static int PrefixLength(string p) => p.IndexOf("://", StringComparison.Ordinal) + 3;

    /// <summary>입력 경로를 정규화한다. 상대 경로는 baseDir 기준.</summary>
    public static string Normalize(string path, string? baseDir = null)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (IsVirtual(p)) return NormalizeVirtual(p);
        if (IsVirtual(baseDir) && !Path.IsPathRooted(p)) return NormalizeVirtual(baseDir + "/" + p);

        if (p.Length == 2 && p[1] == ':') p += "\\";
        if (!Path.IsPathRooted(p) && baseDir != null) p = Path.Combine(baseDir, p);
        return TrimEnd(Path.GetFullPath(p));
    }

    private static string NormalizeVirtual(string p)
    {
        int n = PrefixLength(p);
        var segs = new List<string>();
        foreach (var s in p.Substring(n).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == ".") continue;
            if (s == "..")
            {
                if (segs.Count > 1) segs.RemoveAt(segs.Count - 1);
                continue;
            }
            segs.Add(s);
        }
        return Scheme(p) + "://" + string.Join("/", segs);
    }

    /// <summary>끝의 구분자 제거 (루트 "C:\" 는 유지)</summary>
    public static string TrimEnd(string p)
        => IsVirtual(p) ? (p.Length > PrefixLength(p) ? p.TrimEnd('/') : p) : TrimLocal(p);

    /// <summary>끝의 구분자 제거. "C:\" 같은 드라이브 루트는 유지한다 (Path.TrimEndingDirectorySeparator 대체).</summary>
    private static string TrimLocal(string p)
    {
        if (p.Length <= 1 || !(p.EndsWith('\\') || p.EndsWith('/'))) return p;
        var t = p.TrimEnd('\\', '/');
        if (t.Length == 0) return p;
        return t.Length == 2 && t[1] == ':' ? t + "\\" : t;
    }

    /// <summary>같은 경로인가. FTP 는 서버가 대소문자를 구분할 수 있지만 표시·비교 편의상 구분하지 않는다.</summary>
    public static bool Same(string? a, string? b)
        => a != null && b != null && string.Equals(TrimEnd(a), TrimEnd(b), StringComparison.OrdinalIgnoreCase);

    public static string? Parent(string p)
    {
        if (!IsVirtual(p)) return Path.GetDirectoryName(TrimEnd(p));
        var t = TrimEnd(p);
        int idx = t.LastIndexOf('/');
        return idx < PrefixLength(t) ? null : t.Substring(0, idx);
    }

    public static bool IsRoot(string p) => Parent(p) == null;

    public static string LastSegment(string p)
    {
        if (!IsVirtual(p)) return Path.GetFileName(TrimEnd(p));
        var t = TrimEnd(p);
        return t.Substring(t.LastIndexOf('/') + 1);
    }

    public static string WithSlash(string p)
    {
        char sep = IsVirtual(p) ? '/' : '\\';
        return p.EndsWith(sep) ? p : p + sep;
    }

    /// <summary>"C:\a" → "C:\", "\\srv\share\a" → "\\srv\share", "ftp://서버/a" → "ftp://서버"</summary>
    public static string Root(string p)
    {
        if (!IsVirtual(p)) return Path.GetPathRoot(p) ?? p;
        var t = TrimEnd(p);
        int idx = t.IndexOf('/', PrefixLength(t));
        return idx < 0 ? t : t.Substring(0, idx);
    }

    /// <summary>원격 경로에서 "scheme://" 다음의 이름(기기·연결 이름)</summary>
    public static string RootName(string p) => Root(p).Substring(PrefixLength(p));

    /// <summary>원격 경로에서 루트 아래의 세그먼트들</summary>
    public static string[] RemoteSegments(string p)
    {
        var t = TrimEnd(p);
        var rest = t.Substring(PrefixLength(t));
        return rest.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
    }

    public static string Combine(string dir, string name)
        => IsVirtual(dir) ? TrimEnd(dir) + "/" + name.Replace('\\', '/').Trim('/') : Path.Combine(dir, name);

    public static bool IsUnder(string child, string parent)
        => child.StartsWith(WithSlash(TrimEnd(parent)), StringComparison.OrdinalIgnoreCase);

    public static bool SameVolume(string a, string b)
        => string.Equals(TrimEnd(Root(a)), TrimEnd(Root(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>"\\server" 처럼 공유 이름이 빠진 UNC 경로</summary>
    public static bool IsServerOnly(string p)
    {
        if (!p.StartsWith(@"\\") || p.StartsWith(@"\\?\")) return false;
        return TrimEnd(p).Substring(2).IndexOf('\\') < 0;
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes:N0} B" : $"{v:0.#} {units[u]}";
    }
}
