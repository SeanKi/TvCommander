using System.Globalization;
using System.Text;

namespace MPCommander.Model;

/// <summary>
/// 다국어. 원문(한국어)을 그대로 키로 쓴다 — 코드에서는 Loc.T("원문"), 보간 문자열은 Loc.F($"원문 {값}").
/// 언어 팩은 프로그램 폴더의 lang\&lt;코드&gt;.txt (예: en.txt):
/// <code>
/// #name=English
/// 원문
/// => translation
/// </code>
/// 줄 바꿈은 \n, 탭은 \t, 역슬래시는 \\ 로 쓴다. 보간 문자열의 키는 {0}, {1:N0} 같은 자리 표시 형식이다.
/// 한국어(ko)는 원문 그대로라 팩이 없어도 된다. 번역이 없는 문장은 원문을 보여 준다.
/// XAML 의 글자(Text·Content·ToolTip·Header·Title)는 창을 만든 뒤 Loc.Apply(창) 가 바꾼다.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Missing = new(StringComparer.Ordinal);

    /// <summary>지금 언어 코드 (ko, en ...)</summary>
    public static string Language { get; private set; } = "ko";
    public static bool IsSource => Language == "ko";

    /// <summary>언어 팩 폴더 (프로그램 폴더의 lang)</summary>
    public static string PackFolder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang");

    /// <summary>설정값(auto / ko / en ...)으로 언어를 정한다. auto 는 운영체제 언어 (한국어면 ko, 아니면 en).</summary>
    public static void Init(string? setting)
    {
        var lang = Compat.IsBlank(setting) || setting == "auto" ? SystemLanguage() : setting!.Trim().ToLowerInvariant();
        Language = lang;
        _map = new Dictionary<string, string>(StringComparer.Ordinal);
        Missing.Clear();
        if (lang == "ko") { LoadPack("ko", optional: true); return; }
        if (!LoadPack(lang, optional: false)) Language = "ko";   // 팩이 없으면 원문(한국어)
    }

    /// <summary>운영체제 표시 언어: 한국어면 ko, 아니면 en</summary>
    public static string SystemLanguage()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";

    /// <summary>lang 폴더의 언어 팩 목록: (코드, 표시 이름)</summary>
    public static List<(string Code, string Name)> AvailablePacks()
    {
        var list = new List<(string, string)> { ("ko", "한국어") };
        try
        {
            if (Directory.Exists(PackFolder))
                foreach (var f in Directory.GetFiles(PackFolder, "*.txt"))
                {
                    var code = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (code == "ko") continue;
                    string name = code;
                    foreach (var line in File.ReadLines(f, Encoding.UTF8).Take(20))
                        if (line.StartsWith("#name=")) { name = line.Substring(6).Trim(); break; }
                    list.Add((code, name));
                }
        }
        catch { }
        return list;
    }

    private static bool LoadPack(string lang, bool optional)
    {
        var file = Path.Combine(PackFolder, lang + ".txt");
        if (!File.Exists(file)) return false;
        try
        {
            string? key = null;
            foreach (var raw in File.ReadLines(file, Encoding.UTF8))
            {
                if (raw.Length == 0 || raw.StartsWith("#")) { key = null; continue; }
                if (raw.StartsWith("=>"))
                {
                    if (key != null)
                    {
                        var v = raw.Substring(2);
                        if (v.StartsWith(" ")) v = v.Substring(1);   // "=> " 의 한 칸만 (앞 공백이 뜻이 있는 값도 있다)
                        var value = Unescape(v);
                        if (value.Length > 0) _map[key] = value;
                    }
                    key = null;
                }
                else
                {
                    key = Unescape(raw);
                }
            }
            return true;
        }
        catch
        {
            return optional;
        }
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n });
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Escape(string s)
        => s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    /// <summary>번역 (없으면 원문)</summary>
    public static string T(string text)
    {
        if (IsSource || string.IsNullOrEmpty(text) || !HasHangul(text)) return text;   // 원문(한국어)이 아닌 글자(아이콘·경로 등)는 그대로
        if (_map.TryGetValue(text, out var t)) return t;
        NoteMissing(text);
        return text;
    }

    private static bool HasHangul(string s)
    {
        foreach (char c in s) if (c >= '\uAC00' && c <= '\uD7A3' || c >= '\u3131' && c <= '\u318E') return true;
        return false;
    }

    /// <summary>보간 문자열 번역: 키는 자리 표시 형식 ("{0}개 항목")</summary>
    public static string F(FormattableString fs)
    {
        var format = fs.Format;
        if (!IsSource)
        {
            if (_map.TryGetValue(format, out var t)) format = t;
            else NoteMissing(format);
        }
        try { return string.Format(CultureInfo.CurrentCulture, format, fs.GetArguments()); }
        catch (FormatException) { return fs.ToString(CultureInfo.CurrentCulture); }   // 번역의 자리 표시가 잘못되면 원문
    }

    /// <summary>MPC_LANG_MISSING 이 있으면 번역이 없는 원문을 그 파일에 모은다 (언어 팩 만들 때)</summary>
    private static void NoteMissing(string text)
    {
        if (Environment.GetEnvironmentVariable("MPC_LANG_MISSING") is not { Length: > 0 } file) return;
        lock (Missing)
        {
            if (!Missing.Add(text)) return;
            try { File.AppendAllText(file, Escape(text) + "\r\n=> \r\n\r\n", Encoding.UTF8); } catch { }
        }
    }
}
