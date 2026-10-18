using System.Text;

namespace MPCommander.IO;

/// <summary>텍스트 편집기의 인코딩 — 읽은 그대로 저장한다</summary>
public enum TextEncodingKind { Utf8, Utf8Bom, Cp949, Utf16Le, Utf16Be }

/// <summary>텍스트 파일 읽기·쓰기: 인코딩 알아내기(BOM → 올바른 UTF-8 → CP949), 저장은 임시 파일에 쓴 뒤 바꿔 넣기</summary>
public static class TextFileCodec
{
    /// <summary>편집기로 열 수 있는 최대 크기 (그보다 크면 F3 뷰어)</summary>
    public const long MaxEditBytes = 64L * 1024 * 1024;

    public static string Name(TextEncodingKind k) => k switch
    {
        TextEncodingKind.Utf8Bom => "UTF-8 (BOM)",
        TextEncodingKind.Cp949 => "CP949",
        TextEncodingKind.Utf16Le => "UTF-16 LE",
        TextEncodingKind.Utf16Be => "UTF-16 BE",
        _ => "UTF-8",
    };

    public static (string Text, TextEncodingKind Kind) Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return (new UTF8Encoding(false).GetString(b, 3, b.Length - 3), TextEncodingKind.Utf8Bom);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return (new UnicodeEncoding(false, false).GetString(b, 2, b.Length - 2), TextEncodingKind.Utf16Le);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return (new UnicodeEncoding(true, false).GetString(b, 2, b.Length - 2), TextEncodingKind.Utf16Be);
        try { return (new UTF8Encoding(false, true).GetString(b), TextEncodingKind.Utf8); }
        catch (DecoderFallbackException) { return (Encoding.GetEncoding(949).GetString(b), TextEncodingKind.Cp949); }
    }

    /// <summary>나타낼 수 없는 글자(CP949)가 있으면 EncoderFallbackException</summary>
    public static byte[] Encode(string text, TextEncodingKind k)
    {
        Encoding enc = k switch
        {
            TextEncodingKind.Utf8Bom => new UTF8Encoding(true),
            TextEncodingKind.Cp949 => Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback),
            TextEncodingKind.Utf16Le => new UnicodeEncoding(false, true),
            TextEncodingKind.Utf16Be => new UnicodeEncoding(true, true),
            _ => new UTF8Encoding(false),
        };
        return enc.GetPreamble().Concat(enc.GetBytes(text)).ToArray();
    }

    /// <summary>앞부분에 NUL 이 있으면 바이너리 (UTF-16 은 BOM 이 있을 때만 텍스트로 본다)</summary>
    public static bool LooksBinary(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[8192];
        int n = fs.Read(buf, 0, buf.Length);
        if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return false;
        return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
    }

    /// <summary>같은 폴더의 임시 파일에 다 쓴 뒤 바꿔 넣는다 (중간에 실패해도 원래 파일이 깨지지 않는다)</summary>
    public static void SaveAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".mpc-tmp";
        File.WriteAllBytes(tmp, bytes);
        if (File.Exists(path)) File.Replace(tmp, path, null, true);
        else File.Move(tmp, path);
    }
}
