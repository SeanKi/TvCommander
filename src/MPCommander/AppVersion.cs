using System.Reflection;
using System.Security.Cryptography;

namespace MPCommander;

/// <summary>
/// 창 제목에 쓰는 버전 문자열.
///  커밋된 빌드:  "0.2.0 (b7a588f)"
///  미커밋 빌드:  "0.2.0 (b7a588f+ · md5 1a2b3c4d)"  — 버전은 커밋할 때만 올리므로 md5 로 구분한다.
/// </summary>
public static class AppVersion
{
    public static string Display { get; } = Build();

    private static string Build()
    {
        var asm = typeof(AppVersion).Assembly;
        var v = asm.GetName().Version ?? new Version(0, 0, 0);
        var text = $"{v.Major}.{v.Minor}.{v.Build}";

        string? Meta(string key) => asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                                       .FirstOrDefault(a => a.Key == key)?.Value;
        var commit = Meta("GitCommit");
        bool dirty = string.Equals(Meta("GitDirty"), "true", StringComparison.OrdinalIgnoreCase);
        if (Compat.IsEmpty(commit)) return text;

        if (!dirty) return $"{text} ({commit})";
        return $"{text} ({commit}+ · md5 {AssemblyMd5(asm)})";
    }

    private static string AssemblyMd5(Assembly asm)
    {
        try
        {
            using var md5 = MD5.Create();
            using var fs = File.OpenRead(asm.Location);
            return BitConverter.ToString(md5.ComputeHash(fs)).Replace("-", "").Substring(0, 8).ToLowerInvariant();
        }
        catch
        {
            return "?";
        }
    }
}
