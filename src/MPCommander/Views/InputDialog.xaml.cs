using System.Windows;

namespace MPCommander.Views;

public partial class InputDialog : Window
{
    private InputDialog()
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
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

    /// <summary>입력과 함께 체크 상자 하나를 받는다 (option 은 처음 값 → 고른 값). 취소하면 null.</summary>
    public static string? Show(Window? owner, string title, string prompt, string text, string optionText, string? optionTip, ref bool option)
    {
        var dlg = new InputDialog { Title = title, Owner = owner };
        dlg.PromptText.Text = prompt;
        dlg.InputBox.Text = text;
        dlg.OptionCheck.Content = optionText;
        dlg.OptionCheck.ToolTip = optionTip;
        dlg.OptionCheck.IsChecked = option;
        dlg.OptionCheck.Visibility = Visibility.Visible;
        dlg.Loaded += (_, _) =>
        {
            dlg.InputBox.Focus();
            dlg.InputBox.SelectAll();
        };
        if (dlg.ShowDialog() != true) return null;
        option = dlg.OptionCheck.IsChecked == true;
        return dlg.InputBox.Text;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
