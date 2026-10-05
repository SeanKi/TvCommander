using System.Windows;
using System.Windows.Controls;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>차단 해제 대상 확인 + 설정 (하위 폴더, 파일 형식). 확인을 누르면 설정을 저장한다.</summary>
public partial class UnblockDialog : Window
{
    /// <summary>자주 쓰는 실행·스크립트·문서 형식 (체크박스로 표시)</summary>
    private static readonly string[] CommonTypes = ["exe", "dll", "msi", "ocx", "sys", "ps1", "bat", "cmd", "vbs", "js", "chm", "zip", "7z", "docx", "xlsx", "pdf"];

    private readonly AppSettings _settings;

    private UnblockDialog(AppSettings settings, string targetText)
    {
        InitializeComponent();
        _settings = settings;
        TargetText.Text = targetText;
        RecursiveCheck.IsChecked = settings.UnblockRecursive;
        AllFilesCheck.IsChecked = settings.UnblockAllFiles;

        var selected = settings.UnblockTypes.SplitTrim(';').Select(t => t.TrimStart('*', '.').ToLowerInvariant()).ToHashSet();
        foreach (var t in CommonTypes)
            TypePanel.Children.Add(new CheckBox { Content = t, Tag = t, IsChecked = selected.Contains(t) });
        OtherTypesBox.Text = string.Join(";", selected.Where(t => !CommonTypes.Contains(t)));
        AllFiles_Changed(this, new RoutedEventArgs());
    }

    /// <summary>확인하면 true. 고른 값은 settings 에 반영된다 (파일 저장은 호출한 쪽에서).</summary>
    public static bool Show(Window owner, AppSettings settings, string targetText)
        => new UnblockDialog(settings, targetText) { Owner = owner }.ShowDialog() == true;

    private void AllFiles_Changed(object sender, RoutedEventArgs e)
    {
        if (TypePanel == null) return;
        bool all = AllFilesCheck.IsChecked == true;
        TypePanel.IsEnabled = !all;
        OtherTypesBox.IsEnabled = !all;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var types = TypePanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag)
            .Concat(OtherTypesBox.Text.SplitTrim(';').Select(t => t.TrimStart('*', '.').ToLowerInvariant()))
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        if (AllFilesCheck.IsChecked != true && types.Count == 0)
        {
            ErrorText.Text = "파일 형식을 하나 이상 고르거나 '모든 파일'을 켜세요.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        _settings.UnblockRecursive = RecursiveCheck.IsChecked == true;
        _settings.UnblockAllFiles = AllFilesCheck.IsChecked == true;
        _settings.UnblockTypes = string.Join(";", types);
        DialogResult = true;
    }
}
