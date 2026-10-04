using System.Windows;

namespace TvCommander.Views;

public partial class InputDialog : Window
{
    private InputDialog()
    {
        InitializeComponent();
    }

    /// <summary>입력을 받는다. 취소하면 null.</summary>
    public static string? Show(Window? owner, string title, string prompt, string text, int selectStart = 0, int selectLength = -1)
    {
        var dlg = new InputDialog { Title = title, Owner = owner };
        dlg.PromptText.Text = prompt;
        dlg.InputBox.Text = text;
        dlg.Loaded += (_, _) =>
        {
            dlg.InputBox.Focus();
            dlg.InputBox.Select(selectStart, selectLength < 0 ? text.Length : selectLength);
        };
        return dlg.ShowDialog() == true ? dlg.InputBox.Text : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
