using System.Globalization;
using System.Runtime.Serialization.Json;

namespace MPCommander.Model;

public sealed class PanelState
{
    public string? Path { get; set; }
    public SortColumn Sort { get; set; } = SortColumn.Name;
    public bool Descending { get; set; }
}

/// <summary>
/// 앱 설정. %APPDATA%\MP-Commander\settings.ini 에 저장한다 (추가 라이브러리 없이 읽고 쓰기 위해 INI).
/// 이전 버전의 settings.json 이 있으면 처음 한 번 옮겨 온다.
/// </summary>
public sealed class AppSettings
{
    private const string Header = "MP-Commander 설정 (프로그램 종료 시 저장됨)";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public int PanelCount { get; set; } = 2;
    /// <summary>목록 글씨 크기: 0 작게, 1 보통, 2 크게</summary>
    public int FontLevel { get; set; } = 1;
    public int ActivePanel { get; set; }
    public bool ShowHidden { get; set; }
    /// <summary>F4 편집기</summary>
    public string Editor { get; set; } = "notepad.exe";
    /// <summary>F9 터미널. "auto" = Windows Terminal 이 있으면 wt, 없으면 cmd</summary>
    public string Terminal { get; set; } = "auto";
    public List<PanelState> Panels { get; set; } = new();
    /// <summary>명령줄 이전 명령 (최근 것이 앞)</summary>
    public List<string> CommandHistory { get; set; } = new();

    /// <summary>차단 해제: 폴더를 고르면 하위 폴더까지</summary>
    public bool UnblockRecursive { get; set; } = true;
    /// <summary>차단 해제: 폴더 안에서 대상으로 할 확장자 (; 구분, 점 없이)</summary>
    public string UnblockTypes { get; set; } = "exe;dll";
    /// <summary>차단 해제: 폴더 안의 모든 파일 (UnblockTypes 무시)</summary>
    public bool UnblockAllFiles { get; set; }

    /// <summary>압축 형식: "7z" 또는 "zip"</summary>
    public string PackFormat { get; set; } = "7z";
    /// <summary>압축 수준: 0 저장, 1 빠르게, 2 보통, 3 최고</summary>
    public int PackLevel { get; set; } = 2;
    /// <summary>폴더 하나를 압축할 때 폴더는 빼고 안의 내용만</summary>
    public bool PackContentsOnly { get; set; }

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool Maximized { get; set; }

    private static string Folder => AppPaths.DataFolder;
    private static string IniPath => System.IO.Path.Combine(Folder, "settings.ini");
    private static string LegacyJsonPath => System.IO.Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(IniPath)) return FromIni(IniFile.Load(IniPath));
            if (File.Exists(LegacyJsonPath) && FromLegacyJson() is { } migrated)
            {
                migrated.Save();
                File.Move(LegacyJsonPath, LegacyJsonPath + ".migrated");
                return migrated;
            }
        }
        catch { /* 손상된 설정은 무시 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var ini = new IniFile();
            var s = ini.Section("Settings");
            void Put(string key, object value) => s.Add(new(key, Convert.ToString(value, Inv) ?? ""));
            Put("PanelCount", PanelCount);
            Put("FontLevel", FontLevel);
            Put("ActivePanel", ActivePanel);
            Put("ShowHidden", ShowHidden);
            Put("Editor", Editor);
            Put("Terminal", Terminal);
            Put("WindowLeft", WindowLeft.ToString("R", Inv));
            Put("WindowTop", WindowTop.ToString("R", Inv));
            Put("WindowWidth", WindowWidth.ToString("R", Inv));
            Put("WindowHeight", WindowHeight.ToString("R", Inv));
            Put("Maximized", Maximized);

            var u = ini.Section("Unblock");
            u.Add(new("Recursive", UnblockRecursive.ToString(Inv)));
            u.Add(new("Types", UnblockTypes));
            u.Add(new("AllFiles", UnblockAllFiles.ToString(Inv)));

            var pk = ini.Section("Pack");
            pk.Add(new("Format", PackFormat));
            pk.Add(new("Level", PackLevel.ToString(Inv)));
            pk.Add(new("ContentsOnly", PackContentsOnly.ToString(Inv)));

            for (int i = 0; i < Panels.Count; i++)
            {
                var p = ini.Section($"Panel{i + 1}");
                p.Add(new("Path", Panels[i].Path ?? ""));
                p.Add(new("Sort", Panels[i].Sort.ToString()));
                p.Add(new("Descending", Panels[i].Descending.ToString(Inv)));
            }

            var h = ini.Section("CommandHistory");
            for (int i = 0; i < CommandHistory.Count; i++) h.Add(new((i + 1).ToString(Inv), CommandHistory[i]));

            ini.Save(IniPath, Header);
        }
        catch { /* 설정 저장 실패는 치명적이지 않음 */ }
    }

    private static AppSettings FromIni(IniFile ini)
    {
        var a = new AppSettings();
        string? Get(string key) => ini.Get("Settings", key);
        int Int(string key, int fallback) => int.TryParse(Get(key), NumberStyles.Integer, Inv, out var v) ? v : fallback;
        bool Bool(string key) => bool.TryParse(Get(key), out var v) && v;
        double Dbl(string key, double fallback) => double.TryParse(Get(key), NumberStyles.Float, Inv, out var v) ? v : fallback;

        a.PanelCount = Int("PanelCount", a.PanelCount);
        a.FontLevel = Int("FontLevel", a.FontLevel);
        a.ActivePanel = Int("ActivePanel", 0);
        a.ShowHidden = Bool("ShowHidden");
        a.Editor = Get("Editor") is { Length: > 0 } ed ? ed : a.Editor;
        a.Terminal = Get("Terminal") is { Length: > 0 } te ? te : a.Terminal;
        a.WindowLeft = Dbl("WindowLeft", double.NaN);
        a.WindowTop = Dbl("WindowTop", double.NaN);
        a.WindowWidth = Dbl("WindowWidth", a.WindowWidth);
        a.WindowHeight = Dbl("WindowHeight", a.WindowHeight);
        a.Maximized = Bool("Maximized");
        a.UnblockRecursive = !bool.TryParse(ini.Get("Unblock", "Recursive"), out var ur) || ur;
        a.UnblockTypes = ini.Get("Unblock", "Types") ?? a.UnblockTypes;
        a.UnblockAllFiles = bool.TryParse(ini.Get("Unblock", "AllFiles"), out var ua) && ua;
        a.PackFormat = ini.Get("Pack", "Format") is "zip" ? "zip" : "7z";
        a.PackLevel = int.TryParse(ini.Get("Pack", "Level"), NumberStyles.Integer, Inv, out var pl) ? pl : a.PackLevel;
        a.PackContentsOnly = bool.TryParse(ini.Get("Pack", "ContentsOnly"), out var pc) && pc;

        for (int i = 1; i <= 8; i++)
        {
            var path = ini.Get($"Panel{i}", "Path");
            if (path == null) break;
            a.Panels.Add(new PanelState
            {
                Path = path.Length > 0 ? path : null,
                Sort = Enum.TryParse<SortColumn>(ini.Get($"Panel{i}", "Sort"), out var sort) ? sort : SortColumn.Name,
                Descending = bool.TryParse(ini.Get($"Panel{i}", "Descending"), out var d) && d,
            });
        }
        a.CommandHistory = ini.Section("CommandHistory").Select(kv => kv.Value).Where(v => v.Length > 0).ToList();
        return a;
    }

    /// <summary>0.4.0 이전(.NET 8)의 settings.json 읽기. 생성자가 돌지 않으므로 빈 값은 기본값으로 채운다.</summary>
    private static AppSettings? FromLegacyJson()
    {
        try
        {
            using var fs = File.OpenRead(LegacyJsonPath);
            if (new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(fs) is not AppSettings old) return null;
            var a = new AppSettings
            {
                PanelCount = old.PanelCount == 0 ? 2 : old.PanelCount,
                FontLevel = old.FontLevel,
                ActivePanel = old.ActivePanel,
                ShowHidden = old.ShowHidden,
                Editor = Compat.IsEmpty(old.Editor) ? "notepad.exe" : old.Editor,
                Terminal = Compat.IsEmpty(old.Terminal) ? "auto" : old.Terminal,
                Panels = old.Panels ?? new(),
                CommandHistory = old.CommandHistory ?? new(),
                WindowLeft = old.WindowLeft,
                WindowTop = old.WindowTop,
                WindowWidth = old.WindowWidth > 0 ? old.WindowWidth : 1280,
                WindowHeight = old.WindowHeight > 0 ? old.WindowHeight : 800,
                Maximized = old.Maximized,
            };
            return a;
        }
        catch
        {
            return null;
        }
    }
}
