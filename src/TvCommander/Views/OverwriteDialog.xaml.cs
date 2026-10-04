using System.Windows;
using TvCommander.IO;
using TvCommander.Model;

namespace TvCommander.Views;

public partial class OverwriteDialog : Window
{
    private ConflictChoice _choice = ConflictChoice.Cancel;

    private OverwriteDialog()
    {
        InitializeComponent();
    }

    public static ConflictChoice Ask(Window owner, ConflictInfo info)
    {
        var dlg = new OverwriteDialog { Owner = owner };
        dlg.DstText.Text = $"{info.Destination}\n{PathUtil.FormatSize(info.DestinationSize)} ({info.DestinationSize:N0} 바이트),  {info.DestinationTime:yyyy-MM-dd HH:mm:ss}";
        dlg.SrcText.Text = $"{info.Source}\n{PathUtil.FormatSize(info.SourceSize)} ({info.SourceSize:N0} 바이트),  {info.SourceTime:yyyy-MM-dd HH:mm:ss}";
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        _choice = Enum.Parse<ConflictChoice>((string)((FrameworkElement)sender).Tag);
        DialogResult = true;
    }
}
