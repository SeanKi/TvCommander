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
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _s = settings;

        // 언어: 자동(운영체제) + lang 폴더의 팩
        LangCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = Loc.T("자동 (운영체제 언어)"), Tag = "auto" });
        foreach (var (code, name) in Loc.AvailablePacks())
            LangCombo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"{name} ({code})", Tag = code });
        LangCombo.SelectedIndex = Math.Max(0, LangCombo.Items.Cast<System.Windows.Controls.ComboBoxItem>().ToList()
            .FindIndex(i => (string)i.Tag == (_s.Language ?? "auto")));

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
        var lang = (LangCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "auto";
        if (lang != (_s.Language ?? "auto"))
        {
            _s.Language = lang;
            MessageBox.Show(this, "언어는 프로그램을 다시 시작하면 바뀝니다.\nThe language changes after restarting the program.",
                "MP-Commander", MessageBoxButton.OK, MessageBoxImage.Information);
        }
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

        _s.Editor = EditorBox.Text.Trim();   // 비우면 내장 편집기
        _s.Terminal = Compat.IsBlank(TerminalBox.Text) ? "auto" : TerminalBox.Text.Trim();
        DialogResult = true;
    }
}
