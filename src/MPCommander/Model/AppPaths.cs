namespace MPCommander.Model;

/// <summary>설정·히스토리 폴더 (%APPDATA%\MP-Commander)</summary>
public static class AppPaths
{
    public static string DataFolder { get; } = Init();

    /// <summary>
    /// 이전 이름(TvCommander)으로 쓰던 설정이 있으면 처음 한 번 복사해 온다. 원본은 그대로 둔다.
    /// </summary>
    private static string Init()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "MP-Commander");
        var legacy = Path.Combine(appData, "TvCommander");
        if (Directory.Exists(dir) || !Directory.Exists(legacy)) return dir;

        try
        {
            Directory.CreateDirectory(dir);
            foreach (var file in Directory.GetFiles(legacy))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("TvCommander.ini", StringComparison.OrdinalIgnoreCase)) name = "MP-Commander.ini";
                File.Copy(file, Path.Combine(dir, name), overwrite: false);
            }
        }
        catch
        {
            /* 옮기지 못하면 새 설정으로 시작 */
        }
        return dir;
    }
}
