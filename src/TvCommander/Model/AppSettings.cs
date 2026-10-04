using System.Text.Json;

namespace TvCommander.Model;

public sealed class PanelState
{
    public string? Path { get; set; }
    public SortColumn Sort { get; set; } = SortColumn.Name;
    public bool Descending { get; set; }
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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

    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;
    public bool Maximized { get; set; }

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TvCommander", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* 손상된 설정은 무시 */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { /* 설정 저장 실패는 치명적이지 않음 */ }
    }
}
