using System.Windows;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>즐겨찾기 고치기: 이름(설명)과 경로. 이름이 비면 바에 경로만 보인다</summary>
public partial class FavoriteEditDialog : Window
{
    private (string Path, string Label)? _result;

    private FavoriteEditDialog(string path, string label)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        PathBox.Text = path;
        LabelBox.Text = label;
        Loaded += (_, _) => { LabelBox.Focus(); LabelBox.SelectAll(); };
        UpdatePreview();
    }

    public static (string Path, string Label)? Show(Window owner, string path, string label)
    {
        var d = new FavoriteEditDialog(path, label) { Owner = owner };
        return d.ShowDialog() == true ? d._result : null;
    }

    private void Box_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText == null) return;
        PreviewText.Text = Loc.T("바에 보이는 모양: ") + MainWindow.FavoriteText(PathBox.Text.Trim(), LabelBox.Text.Trim());
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (path.Length == 0) { PathBox.Focus(); return; }
        _result = (path, LabelBox.Text.Trim());
        DialogResult = true;
    }
}
