using System.Windows;
using MPCommander.IO;

namespace MPCommander.Views;

/// <summary>백업 복사 중 사용 중인 파일: 프로그램 닫고 다시 시도 / 강제 종료 / 다시 시도 / 이름 바꿔 교체 / 건너뛰기 / 중단</summary>
public partial class LockedFileDialog : Window
{
    private LockedChoice _choice = LockedChoice.Cancel;

    private LockedFileDialog(string file, IReadOnlyList<LockingProcess> users)
    {
        InitializeComponent();
        FileText.Text = file;
        var others = users.Where(u => !u.IsSelf && !u.IsService).ToList();
        ProcessText.Text = users.Count == 0
            ? "쓰고 있는 프로그램을 찾지 못했습니다 (다른 PC 에서 네트워크로 열었거나 권한 문제일 수 있습니다)."
            : "쓰고 있는 프로그램:\n" + string.Join("\n", users.Select(u => $"  • {u.Display}  (PID {u.Pid})  {u.ExePath}"));
        CloseButton.IsEnabled = KillButton.IsEnabled = others.Count > 0;
        if (others.Count == 0) CloseButton.IsDefault = false;
    }

    public static LockedChoice Ask(Window owner, string file, IReadOnlyList<LockingProcess> users)
    {
        var dlg = new LockedFileDialog(file, users) { Owner = owner };
        dlg.ShowDialog();
        return dlg._choice;
    }

    private void Choice_Click(object sender, RoutedEventArgs e)
    {
        _choice = Enum.TryParse<LockedChoice>((string)((FrameworkElement)sender).Tag, out var c) ? c : LockedChoice.Cancel;
        if (_choice == LockedChoice.KillAndRetry &&
            MessageBox.Show(this, "프로그램을 강제로 끝냅니다. 저장하지 않은 내용은 사라집니다. 계속할까요?", "강제 종료",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        DialogResult = true;
    }
}
