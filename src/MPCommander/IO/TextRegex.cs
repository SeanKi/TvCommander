using System.Text.RegularExpressions;

namespace MPCommander.IO;

/// <summary>뷰어 찾기와 파일 찾기(포함 텍스트)가 함께 쓰는 정규식 규칙</summary>
public static class TextRegex
{
    /// <summary>
    /// .NET 정규식. ^ $ 는 줄 단위이고, Windows 줄 끝(\r\n)에서도 $ 가 맞도록 이스케이프되지 않은 $ 를 (?=\r?$) 로 바꾼다.
    /// 잘못된 식이면 ArgumentException.
    /// </summary>
    public static Regex Create(string pattern, bool matchCase, bool compiled = false)
    {
        var opts = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!matchCase) opts |= RegexOptions.IgnoreCase;
        if (compiled) opts |= RegexOptions.Compiled;
        var fixedPattern = Regex.Replace(pattern, @"(?<!\\)\$", @"(?=\r?$)");
        return new Regex(fixedPattern, opts, TimeSpan.FromSeconds(3));
    }
}
