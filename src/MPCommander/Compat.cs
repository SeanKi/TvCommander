using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MPCommander;

/// <summary>.NET Framework 4.7.2 에 없는 .NET Core API 의 대체</summary>
internal static class Compat
{
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;

    /// <summary>Environment.TickCount64 대체 (단조 증가 밀리초)</summary>
    public static long TickCount64 => (long)(Stopwatch.GetTimestamp() * MsPerTick);

    /// <summary>string.IsNullOrWhiteSpace / IsNullOrEmpty 와 같고, 4.7.2 에서도 null 분석이 되도록 표시만 붙였다.</summary>
    public static bool IsBlank([NotNullWhen(false)] string? s) => string.IsNullOrWhiteSpace(s);
    public static bool IsEmpty([NotNullWhen(false)] string? s) => string.IsNullOrEmpty(s);

    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>File.Move(src, dst, overwrite: true) 대체. 같은 폴더 안에서 대상이 있으면 교체한다.</summary>
    public static void MoveReplace(string src, string dst)
    {
        if (File.Exists(dst)) File.Replace(src, dst, null, ignoreMetadataErrors: true);
        else File.Move(src, dst);
    }

    /// <summary>취소 가능한 TcpClient.ConnectAsync (취소되면 소켓을 닫아 대기를 끝낸다)</summary>
    public static async Task ConnectAsync(TcpClient client, string host, int port, CancellationToken ct)
    {
        using (ct.Register(() => { try { client.Close(); } catch { } }))
            await client.ConnectAsync(host, port).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }
}

/// <summary>.NET Core 의 문자열 오버로드 대체</summary>
internal static class StringCompat
{
    public static bool StartsWith(this string s, char c) => s.Length > 0 && s[0] == c;
    public static bool EndsWith(this string s, char c) => s.Length > 0 && s[s.Length - 1] == c;
    public static bool Contains(this string s, string value, StringComparison comparison) => s.IndexOf(value, comparison) >= 0;
    public static string[] Split(this string s, char separator, StringSplitOptions options) => s.Split(new[] { separator }, options);
    public static string[] Split(this string s, char separator, int count) => s.Split(new[] { separator }, count);

    /// <summary>나누고 각 항목의 공백을 지운 뒤 빈 항목을 뺀다 (StringSplitOptions.TrimEntries 대체)</summary>
    public static string[] SplitTrim(this string s, char separator)
        => s.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
}

/// <summary>
/// 네이티브 코드와 공유하는 고정(pinned) int 배열 (GC.AllocateArray(pinned: true) 대체).
/// 이 객체가 수거될 때 고정을 푼다. 작업 스레드가 참조하는 동안에는 수거되지 않는다.
/// </summary>
internal sealed class PinnedInts
{
    private GCHandle _handle;

    public PinnedInts(int length)
    {
        Values = new int[length];
        _handle = GCHandle.Alloc(Values, GCHandleType.Pinned);
    }

    ~PinnedInts()
    {
        if (_handle.IsAllocated) _handle.Free();
    }

    public int[] Values { get; }

    public unsafe int* Pointer(int index) => (int*)_handle.AddrOfPinnedObject() + index;
}
