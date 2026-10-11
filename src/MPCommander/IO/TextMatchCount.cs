using System.Text;
using System.Text.RegularExpressions;

namespace MPCommander.IO;

/// <summary>
/// 텍스트 파일 디코딩(인코딩 자동 판별)과 '찾은 말 개수 / 찾은 줄 수' 세기.
/// 뷰어와 파일 찾기 결과가 같은 규칙을 쓴다.
/// </summary>
public static class TextMatchCount
{
    /// <summary>BOM → UTF-8 / UTF-16, 없으면 올바른 UTF-8 인지 보고 아니면 한글 ANSI(CP949)</summary>
    public static Encoding DetectEncoding(byte[] d, out int bomLength)
    {
        bomLength = 0;
        if (d.Length >= 3 && d[0] == 0xEF && d[1] == 0xBB && d[2] == 0xBF) { bomLength = 3; return Encoding.UTF8; }
        if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) { bomLength = 2; return Encoding.Unicode; }
        if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) { bomLength = 2; return Encoding.BigEndianUnicode; }

        try
        {
            // 잘린 끝부분의 불완전한 문자는 무시하고 검사
            int len = d.Length;
            int back = 0;
            while (back < 3 && len - back - 1 >= 0 && (d[len - back - 1] & 0xC0) == 0x80) back++;
            if (back < 3 && len - back - 1 >= 0 && d[len - back - 1] >= 0xC0) len = len - back - 1;
            new UTF8Encoding(false, true).GetCharCount(d, 0, len);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(949);
        }
    }

    public static string Decode(byte[] d)
    {
        var enc = DetectEncoding(d, out int bom);
        return enc.GetString(d, bom, d.Length - bom);
    }

    /// <summary>찾은 말의 모든 (위치, 길이). 정규식이면 re, 아니면 find 단순 문자열.</summary>
    public static IEnumerable<(int Pos, int Len)> Matches(string text, string find, bool matchCase, Regex? re)
    {
        if (re != null)
        {
            for (var m = re.Match(text); m.Success; m = m.NextMatch())
            {
                if (m.Length == 0) continue;   // 빈 일치(^, \b 등)는 셀 것이 없다
                int len = m.Length;
                while (len > 0 && text[m.Index + len - 1] is '\r' or '\n') len--;   // 줄 끝 문자는 빼고
                if (len > 0) yield return (m.Index, len);
            }
            yield break;
        }
        if (find.Length == 0) yield break;
        var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int i = text.IndexOf(find, cmp); i >= 0; i = text.IndexOf(find, i + find.Length, cmp))
            yield return (i, find.Length);
    }

    /// <summary>찾은 말 개수와, 그것이 들어 있는 줄 수</summary>
    public static (int Count, int Lines) Count(string text, string find, bool matchCase, Regex? re)
    {
        int count = 0, lines = 0, lineEnd = -1;
        foreach (var (pos, _) in Matches(text, find, matchCase, re))
        {
            count++;
            if (pos > lineEnd)
            {
                lines++;
                lineEnd = text.IndexOf('\n', pos);
                if (lineEnd < 0) lineEnd = int.MaxValue;
            }
        }
        return (count, lines);
    }
}
