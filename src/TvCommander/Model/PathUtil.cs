namespace TvCommander.Model;

/// <summary>
/// 경로 도우미. 일반 Windows 경로와 휴대폰 경로("mtp://기기/저장소/폴더", 구분자 '/') 를 함께 다룬다.
/// </summary>
public static class PathUtil
{
    public const string MtpPrefix = "mtp://";

    public static bool IsMtp(string? p) => p != null && p.StartsWith(MtpPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>입력 경로를 정규화한다. 상대 경로는 baseDir 기준.</summary>
    public static string Normalize(string path, string? baseDir = null)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (IsMtp(p)) return NormalizeMtp(p);
        if (IsMtp(baseDir) && !Path.IsPathRooted(p)) return NormalizeMtp(baseDir + "/" + p);

        if (p.Length == 2 && p[1] == ':') p += "\\";
        if (!Path.IsPathRooted(p) && baseDir != null) p = Path.Combine(baseDir, p);
        return TrimEnd(Path.GetFullPath(p));
    }

    private static string NormalizeMtp(string p)
    {
        var segs = new List<string>();
        foreach (var s in p[MtpPrefix.Length..].Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s == ".") continue;
            if (s == "..")
            {
                if (segs.Count > 1) segs.RemoveAt(segs.Count - 1);
                continue;
            }
            segs.Add(s);
        }
        return MtpPrefix + string.Join('/', segs);
    }

    /// <summary>끝의 구분자 제거 (루트 "C:\" 는 유지)</summary>
    public static string TrimEnd(string p)
        => IsMtp(p) ? (p.Length > MtpPrefix.Length ? p.TrimEnd('/') : p) : Path.TrimEndingDirectorySeparator(p);

    public static bool Same(string? a, string? b)
        => a != null && b != null && string.Equals(TrimEnd(a), TrimEnd(b), StringComparison.OrdinalIgnoreCase);

    public static string? Parent(string p)
    {
        if (!IsMtp(p)) return Path.GetDirectoryName(TrimEnd(p));
        var t = TrimEnd(p);
        int idx = t.LastIndexOf('/');
        return idx < MtpPrefix.Length ? null : t[..idx];
    }

    public static bool IsRoot(string p) => Parent(p) == null;

    public static string LastSegment(string p)
    {
        if (!IsMtp(p)) return Path.GetFileName(TrimEnd(p));
        var t = TrimEnd(p);
        return t[(t.LastIndexOf('/') + 1)..];
    }

    public static string WithSlash(string p)
    {
        char sep = IsMtp(p) ? '/' : '\\';
        return p.EndsWith(sep) ? p : p + sep;
    }

    /// <summary>"C:\a" → "C:\", "\\srv\share\a" → "\\srv\share", "mtp://폰/저장소/a" → "mtp://폰"</summary>
    public static string Root(string p)
    {
        if (!IsMtp(p)) return Path.GetPathRoot(p) ?? p;
        var t = TrimEnd(p);
        int idx = t.IndexOf('/', MtpPrefix.Length);
        return idx < 0 ? t : t[..idx];
    }

    public static string Combine(string dir, string name)
        => IsMtp(dir) ? TrimEnd(dir) + "/" + name.Replace('\\', '/').Trim('/') : Path.Combine(dir, name);

    public static bool IsUnder(string child, string parent)
        => child.StartsWith(WithSlash(TrimEnd(parent)), StringComparison.OrdinalIgnoreCase);

    public static bool SameVolume(string a, string b)
        => string.Equals(TrimEnd(Root(a)), TrimEnd(Root(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>"\\server" 처럼 공유 이름이 빠진 UNC 경로</summary>
    public static bool IsServerOnly(string p)
    {
        if (!p.StartsWith(@"\\") || p.StartsWith(@"\\?\")) return false;
        return TrimEnd(p)[2..].IndexOf('\\') < 0;
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
