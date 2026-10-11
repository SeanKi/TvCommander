using System.Windows;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>설정 창: 백업 복사, 압축/풀기, 차단 해제, 편집기·터미널. 확인하면 settings 에 반영 (파일 저장은 호출한 쪽에서).</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;

    private SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _s = settings;

        BackupRootBox.Text = _s.BackupRoot;
        BackupPrefixBox.Text = _s.BackupPrefix;
        BackupRestartCheck.IsChecked = _s.BackupRestart;

        (_s.PackFormat == "zip" ? PackZip : Pack7z).IsChecked = true;
        PackLevelCombo.SelectedIndex = Math.Max(0, Math.Min(3, _s.PackLevel));
        PackContentsCheck.IsChecked = _s.PackContentsOnly;
        UnpackFolderCheck.IsChecked = _s.UnpackToSubfolder;
        UnpackOverwriteCheck.IsChecked = _s.UnpackOverwrite;

        UnblockRecursiveCheck.IsChecked = _s.UnblockRecursive;
        UnblockTypesBox.Text = _s.UnblockTypes;
        UnblockAllCheck.IsChecked = _s.UnblockAllFiles;

        EditorBox.Text = _s.Editor;
        TerminalBox.Text = _s.Terminal;
    }

    public static bool Show(Window owner, AppSettings settings) => new SettingsWindow(settings) { Owner = owner }.ShowDialog() == true;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _s.BackupRoot = Compat.IsBlank(BackupRootBox.Text) ? "backup" : BackupRootBox.Text.Trim();
        _s.BackupPrefix = BackupPrefixBox.Text.Trim();
        _s.BackupRestart = BackupRestartCheck.IsChecked == true;

        _s.PackFormat = PackZip.IsChecked == true ? "zip" : "7z";
        _s.PackLevel = PackLevelCombo.SelectedIndex;
        _s.PackContentsOnly = PackContentsCheck.IsChecked == true;
        _s.UnpackToSubfolder = UnpackFolderCheck.IsChecked == true;
        _s.UnpackOverwrite = UnpackOverwriteCheck.IsChecked == true;

        _s.UnblockRecursive = UnblockRecursiveCheck.IsChecked == true;
        var types = string.Join(";", UnblockTypesBox.Text.SplitTrim(';').Select(t => t.TrimStart('*', '.').ToLowerInvariant()).Where(t => t.Length > 0).Distinct());
        _s.UnblockTypes = types.Length > 0 ? types : "exe;dll";
        _s.UnblockAllFiles = UnblockAllCheck.IsChecked == true;

        _s.Editor = Compat.IsBlank(EditorBox.Text) ? "notepad.exe" : EditorBox.Text.Trim();
        _s.Terminal = Compat.IsBlank(TerminalBox.Text) ? "auto" : TerminalBox.Text.Trim();
        DialogResult = true;
    }
}
