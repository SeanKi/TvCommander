using System.Globalization;

namespace MPCommander.Model;

public sealed record SmartEntry(string Path, int Visits, double DwellSeconds);

/// <summary>
/// 폴더 히스토리 (모든 패널 공용). MP-Commander.ini 에 저장된다.
///  - 최근 폴더: 폴더를 떠날 때 맨 앞에 추가, MaxHistory 개까지
///  - 스마트 히스토리: 머문 시간과 방문 횟수로 점수를 매겨 상위 SmartCount 개.
///    오래 안 간 폴더는 점수가 반감기(SmartHalfLifeDays)에 따라 줄어든다.
/// </summary>
public sealed class DirectoryHistory
{
    private const string FileHeader =
        "MP-Commander 히스토리\n" +
        "[History] MaxHistory: 최근 폴더 최대 개수 (5~500)\n" +
        "          SmartCount: 자주 머문 폴더 개수 (0~20, 0 이면 끔)\n" +
        "          SmartHalfLifeDays: 이 일수가 지나면 점수가 절반 (1~365)\n" +
        "          MaxDwellMinutesPerVisit: 한 번 방문에서 인정하는 최대 체류 시간 (1~600)\n" +
        "[Recent], [Stats] 는 프로그램이 관리한다. Stats = 경로|방문수|체류초|마지막방문(UTC)";

    private sealed class Stat
    {
        public required string Path;
        public int Visits;
        public double DwellSeconds;
        public DateTime LastUtc;
    }

    private readonly string _file;
    private readonly IniFile _ini;
    private readonly List<string> _recent = new();
    private readonly Dictionary<string, Stat> _stats = new(StringComparer.OrdinalIgnoreCase);

    private DirectoryHistory(string file, IniFile ini)
    {
        _file = file;
        _ini = ini;
    }

    public int MaxHistory { get; private set; } = 30;
    public int SmartCount { get; private set; } = 5;
    public int SmartHalfLifeDays { get; private set; } = 14;
    public int MaxDwellMinutesPerVisit { get; private set; } = 30;
    private int MaxStats => Math.Max(200, MaxHistory * 4);

    public static string DefaultFile => Path.Combine(AppPaths.DataFolder, "MP-Commander.ini");

    public static DirectoryHistory Load(string? file = null)
    {
        file ??= DefaultFile;
        IniFile ini;
        try { ini = IniFile.Load(file); }
        catch { ini = new IniFile(); }

        var h = new DirectoryHistory(file, ini)
        {
            MaxHistory = ini.GetInt("History", "MaxHistory", 30, 5, 500),
            SmartCount = ini.GetInt("History", "SmartCount", 5, 0, 20),
            SmartHalfLifeDays = ini.GetInt("History", "SmartHalfLifeDays", 14, 1, 365),
            MaxDwellMinutesPerVisit = ini.GetInt("History", "MaxDwellMinutesPerVisit", 30, 1, 600),
        };

        foreach (var kv in ini.Section("Recent"))
            if (kv.Value.Length > 0 && !h._recent.Contains(kv.Value, StringComparer.OrdinalIgnoreCase))
                h._recent.Add(kv.Value);
        if (h._recent.Count > h.MaxHistory) h._recent.RemoveRange(h.MaxHistory, h._recent.Count - h.MaxHistory);

        foreach (var kv in ini.Section("Stats"))
        {
            var parts = kv.Value.Split('|');   // '|' 는 Windows 경로에 쓸 수 없는 문자
            if (parts.Length < 4 || parts[0].Length == 0) continue;
            h._stats[parts[0]] = new Stat
            {
                Path = parts[0],
                Visits = int.TryParse(parts[1], out var v) ? v : 0,
                DwellSeconds = double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
                LastUtc = DateTime.TryParse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                    ? t : DateTime.UtcNow,
            };
        }
        return h;
    }

    public void Save()
    {
        var settings = _ini.Section("History");
        settings.Clear();
        settings.Add(new("MaxHistory", MaxHistory.ToString()));
        settings.Add(new("SmartCount", SmartCount.ToString()));
        settings.Add(new("SmartHalfLifeDays", SmartHalfLifeDays.ToString()));
        settings.Add(new("MaxDwellMinutesPerVisit", MaxDwellMinutesPerVisit.ToString()));

        var recent = _ini.Section("Recent");
        recent.Clear();
        for (int i = 0; i < _recent.Count; i++) recent.Add(new((i + 1).ToString(), _recent[i]));

        var stats = _ini.Section("Stats");
        stats.Clear();
        int n = 0;
        foreach (var s in _stats.Values.OrderByDescending(Score))
        {
            stats.Add(new((++n).ToString(), string.Join("|", s.Path, s.Visits,
                s.DwellSeconds.ToString("0", CultureInfo.InvariantCulture),
                s.LastUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))));
        }

        try { _ini.Save(_file, FileHeader); }
        catch { /* 히스토리 저장 실패는 치명적이지 않음 */ }
    }

    /// <summary>떠난 폴더를 최근 목록 맨 앞에 넣는다.</summary>
    public void AddRecent(string path)
    {
        _recent.RemoveAll(p => PathUtil.Same(p, path));
        _recent.Insert(0, path);
        if (_recent.Count > MaxHistory) _recent.RemoveRange(MaxHistory, _recent.Count - MaxHistory);
    }

    public void RecordVisit(string path)
    {
        var s = GetStat(path);
        s.Visits++;
        s.LastUtc = DateTime.UtcNow;
        PruneStats();
    }

    public void AddDwell(string path, TimeSpan dwell)
    {
        double secs = Math.Min(dwell.TotalSeconds, MaxDwellMinutesPerVisit * 60.0);
        if (secs <= 0) return;
        var s = GetStat(path);
        s.DwellSeconds += secs;
        s.LastUtc = DateTime.UtcNow;
    }

    /// <summary>자주 머문 폴더. 두 번 이상 왔거나 1분 이상 머문 곳만.</summary>
    public IReadOnlyList<SmartEntry> GetSmart()
    {
        if (SmartCount == 0) return Array.Empty<SmartEntry>();
        return _stats.Values
            .Where(s => s.Visits >= 2 || s.DwellSeconds >= 60)
            .OrderByDescending(Score)
            .Take(SmartCount)
            .Select(s => new SmartEntry(s.Path, s.Visits, s.DwellSeconds))
            .ToList();
    }

    /// <summary>최근 폴더 (스마트 목록에 이미 있는 것은 뺀다)</summary>
    public IReadOnlyList<string> GetRecent(IEnumerable<string>? exclude = null)
    {
        var ex = exclude?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _recent.Where(p => ex == null || !ex.Contains(p)).ToList();
    }

    public void Clear()
    {
        _recent.Clear();
        _stats.Clear();
    }

    private Stat GetStat(string path)
    {
        if (!_stats.TryGetValue(path, out var s))
            _stats[path] = s = new Stat { Path = path, LastUtc = DateTime.UtcNow };
        return s;
    }

    /// <summary>점수 = (체류 분 + 방문수 × 0.5) × 0.5^(경과일 / 반감기)</summary>
    private double Score(Stat s)
    {
        double ageDays = Math.Max(0, (DateTime.UtcNow - s.LastUtc).TotalDays);
        return (s.DwellSeconds / 60.0 + s.Visits * 0.5) * Math.Pow(0.5, ageDays / SmartHalfLifeDays);
    }

    private void PruneStats()
    {
        if (_stats.Count <= MaxStats) return;
        foreach (var s in _stats.Values.OrderBy(Score).Take(_stats.Count - MaxStats).ToList())
            _stats.Remove(s.Path);
    }
}
