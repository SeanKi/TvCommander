using System.Globalization;

namespace MPCommander.Model;

public sealed record SmartEntry(string Path, int Visits, double DwellSeconds, bool Pinned);

/// <summary>
/// 폴더 히스토리 (모든 패널 공용). MP-Commander.ini 에 저장된다.
///  - 최근 폴더: 폴더를 떠날 때 맨 앞에 추가, MaxHistory 개까지
///  - 스마트 히스토리: 머문 시간과 방문 횟수로 점수를 매겨 상위 SmartCount 개.
///    오래 안 간 폴더는 점수가 반감기(SmartHalfLifeDays)에 따라 줄어든다.
///  - 별표(★)로 직접 올린 폴더는 "고정"으로 항상 맨 앞에 나오고,
///    별표로 내린 폴더는 자동 순위에도 다시 나오지 않는다 (다시 올리기 전까지).
/// </summary>
public sealed class DirectoryHistory
{
    private const string FileHeader =
        "MP-Commander 히스토리\n" +
        "[History] MaxHistory: 최근 폴더 최대 개수 (5~500)\n" +
        "          SmartCount: 자주 머문 폴더 개수 (0~20, 0 이면 끔)\n" +
        "          SmartHalfLifeDays: 이 일수가 지나면 점수가 절반 (1~365)\n" +
        "          MaxDwellMinutesPerVisit: 한 번 방문에서 인정하는 최대 체류 시간 (1~600)\n" +
        "[Recent], [Stats], [Pinned], [Excluded] 는 프로그램이 관리한다. Stats = 경로|방문수|체류초|마지막방문(UTC)\n" +
        "[Pinned] = 별표로 올린 폴더, [Excluded] = 별표로 내려 자동 순위에서 뺀 폴더";
    private const int MaxExcluded = 500;

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
    private readonly List<string> _pinned = new();   // 최근에 올린 것이 앞
    private readonly List<string> _excluded = new();

    /// <summary>자주 머문 폴더 목록이 바뀌었을 때 (별표 상태 갱신용)</summary>
    public event Action? Changed;

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

        foreach (var kv in ini.Section("Pinned"))
            if (kv.Value.Length > 0 && !h._pinned.Any(p => PathUtil.Same(p, kv.Value))) h._pinned.Add(kv.Value);
        foreach (var kv in ini.Section("Excluded"))
            if (kv.Value.Length > 0 && !h._excluded.Any(p => PathUtil.Same(p, kv.Value))) h._excluded.Add(kv.Value);
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

        var pinned = _ini.Section("Pinned");
        pinned.Clear();
        for (int i = 0; i < _pinned.Count; i++) pinned.Add(new((i + 1).ToString(), _pinned[i]));

        var excluded = _ini.Section("Excluded");
        excluded.Clear();
        for (int i = 0; i < _excluded.Count; i++) excluded.Add(new((i + 1).ToString(), _excluded[i]));

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

    /// <summary>
    /// 자주 머문 폴더: 별표로 고정한 폴더(전부) + 남은 자리(SmartCount)를 자동 순위로 채운다.
    /// 자동 순위 후보는 두 번 이상 왔거나 1분 이상 머문 곳이고, 별표로 내린 폴더는 뺀다.
    /// </summary>
    public IReadOnlyList<SmartEntry> GetSmart()
    {
        var list = new List<SmartEntry>();
        foreach (var p in _pinned)
        {
            _stats.TryGetValue(p, out var st);
            list.Add(new SmartEntry(p, st?.Visits ?? 0, st?.DwellSeconds ?? 0, Pinned: true));
        }

        int room = SmartCount - list.Count;
        if (room <= 0) return list;
        list.AddRange(_stats.Values
            .Where(s => s.Visits >= 2 || s.DwellSeconds >= 60)
            .Where(s => !_pinned.Any(p => PathUtil.Same(p, s.Path)) && !_excluded.Any(p => PathUtil.Same(p, s.Path)))
            .OrderByDescending(Score)
            .Take(room)
            .Select(s => new SmartEntry(s.Path, s.Visits, s.DwellSeconds, Pinned: false)));
        return list;
    }

    /// <summary>현재 자주 머문 폴더 목록에 있는가 (별표 켜짐)</summary>
    public bool IsSmart(string path) => GetSmart().Any(e => PathUtil.Same(e.Path, path));

    /// <summary>
    /// 별표 버튼: 목록에 있으면 내리고(자동 순위에서도 제외), 없으면 고정으로 올린다.
    /// 반환값: 올렸으면 true.
    /// </summary>
    public bool ToggleSmart(string path)
    {
        bool added;
        if (IsSmart(path))
        {
            _pinned.RemoveAll(p => PathUtil.Same(p, path));
            if (!_excluded.Any(p => PathUtil.Same(p, path))) _excluded.Insert(0, path);
            if (_excluded.Count > MaxExcluded) _excluded.RemoveRange(MaxExcluded, _excluded.Count - MaxExcluded);
            added = false;
        }
        else
        {
            _excluded.RemoveAll(p => PathUtil.Same(p, path));
            _pinned.RemoveAll(p => PathUtil.Same(p, path));
            _pinned.Insert(0, path);
            added = true;
        }
        Changed?.Invoke();
        return added;
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
        _pinned.Clear();
        _excluded.Clear();
        Changed?.Invoke();
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
