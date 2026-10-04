using System.Text;

namespace TvCommander.Model;

/// <summary>최소한의 INI 읽기/쓰기. 섹션과 키 순서를 유지한다. ';' 또는 '#' 으로 시작하는 줄은 주석.</summary>
public sealed class IniFile
{
    private readonly List<(string Name, List<KeyValuePair<string, string>> Entries)> _sections = new();

    public static IniFile Load(string path)
    {
        var ini = new IniFile();
        if (!File.Exists(path)) return ini;

        List<KeyValuePair<string, string>>? current = null;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = ini.Section(line[1..^1].Trim());
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq <= 0 || current == null) continue;
            current.Add(new(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return ini;
    }

    public List<KeyValuePair<string, string>> Section(string name)
    {
        foreach (var s in _sections)
            if (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return s.Entries;
        var entries = new List<KeyValuePair<string, string>>();
        _sections.Add((name, entries));
        return entries;
    }

    public string? Get(string section, string key)
    {
        foreach (var kv in Section(section))
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    public int GetInt(string section, string key, int fallback, int min, int max)
        => int.TryParse(Get(section, key), out var v) ? Math.Clamp(v, min, max) : fallback;

    public void Save(string path, string? header = null)
    {
        var sb = new StringBuilder();
        if (header != null)
            foreach (var line in header.Split('\n')) sb.Append("; ").AppendLine(line.TrimEnd());
        foreach (var (name, entries) in _sections)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append('[').Append(name).AppendLine("]");
            foreach (var kv in entries) sb.Append(kv.Key).Append('=').AppendLine(kv.Value);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
        File.Move(tmp, path, overwrite: true);
    }
}
