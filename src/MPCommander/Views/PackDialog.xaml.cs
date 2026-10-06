using System.Windows;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>압축 대화상자: 압축 파일 경로, 형식(7z / ZIP), 압축 수준. 형식·수준은 다음에 다시 쓰도록 설정에 남긴다.</summary>
public partial class PackDialog : Window
{
    private readonly AppSettings _settings;
    private readonly string _baseDir;

    public string ArchivePath { get; private set; } = "";
    public bool Use7z => Format7z.IsChecked == true;
    public int Level => LevelCombo.SelectedIndex;
    /// <summary>폴더 하나를 골랐을 때: 폴더 자체는 빼고 안의 내용만 압축</summary>
    public bool ContentsOnly => ContentsOnlyCheck.Visibility == Visibility.Visible && ContentsOnlyCheck.IsChecked == true;

    private PackDialog(AppSettings settings, string sourceText, string targetDir, string baseName, bool singleFolder)
    {
        InitializeComponent();
        _settings = settings;
        _baseDir = targetDir;
        SourceText.Text = sourceText;

        bool has7z = SevenZip.Exe != null;
        Format7z.IsEnabled = has7z;
        SevenZipText.Text = has7z
            ? $"7-Zip: {SevenZip.Exe}"
            : "7-Zip 을 찾지 못해 7z 형식은 쓸 수 없습니다. 7-Zip 을 설치하거나 7za.exe 를 MP-Commander 폴더에 두세요.";
        LevelCombo.SelectedIndex = Math.Max(0, Math.Min(3, settings.PackLevel));
        if (singleFolder)
        {
            ContentsOnlyCheck.Visibility = Visibility.Visible;
            ContentsOnlyCheck.IsChecked = settings.PackContentsOnly;
        }

        PathBox.Text = Path.Combine(targetDir, baseName + ".zip");
        if (has7z && settings.PackFormat != "zip") Format7z.IsChecked = true;
        else FormatZip.IsChecked = true;

        Loaded += (_, _) =>
        {
            PathBox.Focus();
            // 파일 이름 부분만 골라 두어 바로 고쳐 쓸 수 있게
            int start = PathBox.Text.Length - Path.GetFileName(PathBox.Text).Length;
            PathBox.Select(start, Path.GetFileNameWithoutExtension(PathBox.Text).Length);
        };
    }

    /// <summary>확인하면 대화상자를 돌려준다(경로·형식·수준). 취소하면 null.</summary>
    public static PackDialog? Show(Window owner, AppSettings settings, string sourceText, string targetDir, string baseName, bool singleFolder)
    {
        var dlg = new PackDialog(settings, sourceText, targetDir, baseName, singleFolder) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg : null;
    }

    /// <summary>형식을 바꾸면 확장자도 따라 바꾼다</summary>
    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (PathBox == null) return;
        var path = PathBox.Text.Trim();
        var ext = Use7z ? ".7z" : ".zip";
        var cur = Path.GetExtension(path);
        if (cur.Equals(".zip", StringComparison.OrdinalIgnoreCase) || cur.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            path = path.Substring(0, path.Length - cur.Length);
        PathBox.Text = path + ext;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string path;
        try { path = PathUtil.Normalize(PathBox.Text.Trim(), _baseDir); }
        catch (Exception ex) { ShowError(ex.Message); return; }
        if (Compat.IsBlank(Path.GetFileName(path))) { ShowError("압축 파일 이름을 입력하세요."); return; }
        if (PathUtil.IsVirtual(path)) { ShowError("압축 파일은 PC 의 폴더(로컬·네트워크 드라이브)에만 만들 수 있습니다."); return; }

        var ext = Use7z ? ".7z" : ".zip";
        if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) path += ext;

        ArchivePath = path;
        _settings.PackFormat = Use7z ? "7z" : "zip";
        _settings.PackLevel = Level;
        if (ContentsOnlyCheck.Visibility == Visibility.Visible) _settings.PackContentsOnly = ContentsOnlyCheck.IsChecked == true;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
