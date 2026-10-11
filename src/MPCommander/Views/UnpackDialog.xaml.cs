using System.Windows;
using System.Windows.Controls;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>압축 풀기 대화상자: 풀 위치(기본 대상 패널), 압축 파일마다 폴더 만들기, 덮어쓰기. 옵션은 설정에 남긴다.</summary>
public partial class UnpackDialog : Window
{
    private readonly AppSettings _settings;
    private readonly string _baseDir;
    private readonly IReadOnlyList<string> _archives;

    public string Destination { get; private set; } = "";
    public bool PerArchiveFolder => SubfolderCheck.IsChecked == true;
    public bool Overwrite => OverwriteCheck.IsChecked == true;

    private UnpackDialog(AppSettings settings, IReadOnlyList<string> archives, string sourceDir, string targetDir)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _settings = settings;
        _baseDir = targetDir;
        _archives = archives;

        var names = archives.Select(Path.GetFileName).ToList();
        SourceText.Text = (names.Count <= 5 ? string.Join(", ", names) : string.Join(", ", names.Take(5)) + Loc.F($" 외 {names.Count - 5}개"))
                          + $"  ({sourceDir})";
        PathBox.Text = PathUtil.WithSlash(targetDir);
        SubfolderCheck.IsChecked = settings.UnpackToSubfolder;
        OverwriteCheck.IsChecked = settings.UnpackOverwrite;
        PathBox.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        Loaded += (_, _) => { PathBox.Focus(); PathBox.SelectAll(); };
    }

    /// <summary>확인하면 대화상자를 돌려준다. 취소하면 null.</summary>
    public static UnpackDialog? Show(Window owner, AppSettings settings, IReadOnlyList<string> archives, string sourceDir, string targetDir)
    {
        var dlg = new UnpackDialog(settings, archives, sourceDir, targetDir) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg : null;
    }

    /// <summary>압축 파일 이름에서 폴더 이름: 확장자를 떼고, x.tar.gz 같은 이중 확장자는 .tar 까지 뗀다</summary>
    public static string FolderNameFor(string archivePath)
    {
        var name = Path.GetFileNameWithoutExtension(archivePath);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) && name.Length > 4) name = name.Substring(0, name.Length - 4);
        return name.Length > 0 ? name : Path.GetFileName(archivePath);
    }

    /// <summary>각 압축 파일을 풀 실제 폴더</summary>
    public string TargetFor(string archivePath)
        => PerArchiveFolder ? Path.Combine(Destination, FolderNameFor(archivePath)) : Destination;

    private void Option_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText == null || PathBox == null) return;
        var dest = PathUtil.TrimEnd(PathBox.Text.Trim());
        var first = _archives[0];
        var example = SubfolderCheck.IsChecked == true ? Path.Combine(dest, FolderNameFor(first)) + "\\" : PathUtil.WithSlash(dest);
        PreviewText.Text = Loc.F($"예: {Path.GetFileName(first)} → {example}") + (_archives.Count > 1 ? Loc.F($"   (모두 {_archives.Count}개)") : "");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string dest;
        try { dest = PathUtil.Normalize(PathBox.Text.Trim(), _baseDir); }
        catch (Exception ex) { ShowError(ex.Message); return; }
        if (PathUtil.IsVirtual(dest)) { ShowError(Loc.T("압축은 PC 의 폴더(로컬·네트워크 드라이브)에만 풀 수 있습니다.")); return; }

        Destination = PathUtil.TrimEnd(dest);
        _settings.UnpackToSubfolder = PerArchiveFolder;
        _settings.UnpackOverwrite = Overwrite;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
