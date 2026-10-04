namespace TvCommander.Model;

public static class PathUtil
{
    /// <summary>입력 경로를 정규화한다. 상대 경로는 baseDir 기준.</summary>
    public static string Normalize(string path, string? baseDir = null)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (p.Length == 2 && p[1] == ':') p += "\\";
        if (!Path.IsPathRooted(p) && baseDir != null) p = Path.Combine(baseDir, p);
        return TrimEnd(Path.GetFullPath(p));
    }

    /// <summary>끝의 '\' 제거 (루트 "C:\" 는 유지)</summary>
    public static string TrimEnd(string p) => Path.TrimEndingDirectorySeparator(p);

    public static bool Same(string? a, string? b)
        => a != null && b != null && string.Equals(TrimEnd(a), TrimEnd(b), StringComparison.OrdinalIgnoreCase);

    public static string? Parent(string p) => Path.GetDirectoryName(TrimEnd(p));
    public static bool IsRoot(string p) => Parent(p) == null;
    public static string LastSegment(string p) => Path.GetFileName(TrimEnd(p));
    public static string WithSlash(string p) => p.EndsWith('\\') ? p : p + "\\";
    public static string Root(string p) => Path.GetPathRoot(p) ?? p;

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
