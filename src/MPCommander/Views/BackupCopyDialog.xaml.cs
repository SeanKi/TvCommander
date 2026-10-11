using System.Windows;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 백업 복사 준비 창: 무엇을 어디로 복사·백업하는지 보여 주고, 대상 파일을 쓰는 프로그램을 찾아 닫게 한다.
/// 백업 위치·앞말·다시 실행 옵션은 설정에 남긴다.
/// </summary>
public partial class BackupCopyDialog : Window
{
    private readonly AppSettings _settings;
    private readonly BackupCopyPlan _plan;
    private readonly DateTime _now = DateTime.Now;
    private readonly Dictionary<int, LockingProcess> _seen = new();
    private List<LockingProcess> _current = new();
    private bool _checking;

    public string BackupPath { get; private set; } = "";
    public bool Restart => RestartCheck.IsChecked == true;
    /// <summary>한 번이라도 대상 파일을 쓰고 있던 프로그램 (복사 뒤 다시 실행 후보)</summary>
    public IReadOnlyCollection<LockingProcess> Seen => _seen.Values;

    private BackupCopyDialog(AppSettings settings, BackupCopyPlan plan, string sourceText)
    {
        InitializeComponent();
        _settings = settings;
        _plan = plan;

        SourceText.Text = sourceText;
        TargetText.Text = plan.TargetDirectory;
        int over = plan.Overwrites.Count();
        CountText.Text = $"{plan.Items.Count:N0}개 ({PathUtil.FormatSize(plan.TotalBytes)}) — 덮어쓸 파일 {over:N0}개는 먼저 백업, 새 파일 {plan.Items.Count - over:N0}개";
        RootBox.Text = settings.BackupRoot;
        PrefixBox.Text = settings.BackupPrefix;
        RestartCheck.IsChecked = settings.BackupRestart;
        UpdatePreview();
        Loaded += async (_, _) => await CheckAsync();
    }

    /// <summary>계속을 누르면 대화상자, 취소하면 null</summary>
    public static BackupCopyDialog? Show(Window owner, AppSettings settings, BackupCopyPlan plan, string sourceText)
    {
        var dlg = new BackupCopyDialog(settings, plan, sourceText) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg : null;
    }

    private void Backup_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (BackupPreview == null || PrefixBox == null) return;
        if (!_plan.Overwrites.Any())
        {
            BackupPreview.Text = "덮어쓸 파일이 없어 백업 폴더는 만들지 않습니다.";
            return;
        }
        try
        {
            BackupPreview.Text = $"백업 폴더: {_plan.MakeBackupPath(RootBox.Text.Trim(), PrefixBox.Text, _now)}\\";
        }
        catch (Exception ex)
        {
            BackupPreview.Text = "백업 위치가 올바르지 않습니다: " + ex.Message;
        }
    }

    // ───────────────────────── 실행 중인 프로그램 ─────────────────────────

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        SetBusy(true, "대상 파일을 쓰고 있는 프로그램을 찾는 중...");
        try
        {
            // 덮어쓸 파일만 확인하면 된다 (새로 생기는 파일은 아무도 쓰지 않는다)
            var files = _plan.Overwrites.Select(o => o.Destination).ToList();
            var find = Task.Run(() => RunningProcesses.FindUsing(files));
            if (await Task.WhenAny(find, Task.Delay(15000)) != find)
            {
                SetBusy(false, "확인이 15초 안에 끝나지 않았습니다. 그대로 계속할 수 있습니다 (사용 중인 파일은 복사 중에 묻습니다).");
                return;
            }
            _current = await find;
            foreach (var p in _current) _seen[p.Pid] = p;
            ProcessList.ItemsSource = _current;

            var others = _current.Where(p => !p.IsSelf).ToList();
            string msg = others.Count == 0
                ? "대상 파일을 쓰고 있는 프로그램이 없습니다. [계속]을 누르면 백업 후 복사합니다."
                : $"{others.Count}개 프로그램이 대상 파일을 쓰고 있습니다. 프로그램을 저장·종료한 뒤(또는 [닫기 요청]) [다시 확인] 하고 [계속]을 누르세요.";
            if (_current.Any(p => p.IsSelf))
                msg += "\nMP-Commander 자신이 쓰는 파일은 복사 중에 '이름 바꿔 교체'를 고르면 바꿀 수 있습니다 (다시 시작하면 적용).";
            SetBusy(false, msg);
        }
        catch (Exception ex)
        {
            SetBusy(false, "프로그램 확인 실패: " + ex.Message + " — 그대로 계속할 수 있습니다.");
        }
        finally
        {
            _checking = false;
        }
    }

    private void SetBusy(bool busy, string status)
    {
        ProcessStatus.Text = status;
        RecheckButton.IsEnabled = !busy;
        CloseButton.IsEnabled = KillButton.IsEnabled = !busy && _current.Any(p => !p.IsSelf);
        OkButton.IsEnabled = !busy;
    }

    private List<LockingProcess> Chosen()
    {
        var sel = ProcessList.SelectedItems.Cast<LockingProcess>().ToList();
        return (sel.Count > 0 ? sel : _current).Where(p => !p.IsSelf).ToList();
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private async void CloseProcesses_Click(object sender, RoutedEventArgs e)
    {
        var targets = Chosen();
        if (targets.Count == 0) return;
        SetBusy(true, "닫기를 요청하는 중... (저장 확인이 뜨면 그 프로그램에서 답하세요)");
        var failed = await Task.Run(() => targets.Where(p => !RunningProcesses.CloseGracefully(p, 10000)).ToList());
        await CheckAsync();
        if (failed.Count > 0)
            ProcessStatus.Text += $"\n닫히지 않은 프로그램: {string.Join(", ", failed.Select(f => f.Name))} — 직접 닫거나 [강제 종료] 하세요.";
    }

    private async void KillProcesses_Click(object sender, RoutedEventArgs e)
    {
        var targets = Chosen();
        if (targets.Count == 0) return;
        if (MessageBox.Show(this, $"다음 프로그램을 강제로 끝냅니다. 저장하지 않은 내용은 사라집니다.\n\n{string.Join("\n", targets.Select(t => $"{t.Display}  (PID {t.Pid})"))}",
                "강제 종료", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        SetBusy(true, "강제 종료하는 중...");
        var failed = await Task.Run(() => targets.Where(p => !RunningProcesses.Kill(p)).ToList());
        await CheckAsync();
        if (failed.Count > 0)
            ProcessStatus.Text += $"\n끝내지 못한 프로그램: {string.Join(", ", failed.Select(f => f.Name))} (관리자 권한으로 실행된 프로그램일 수 있습니다)";
    }

    // ───────────────────────── 확인 ─────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string path = "";
        if (_plan.Overwrites.Any())
        {
            try { path = _plan.MakeBackupPath(RootBox.Text.Trim(), PrefixBox.Text, _now); }
            catch (Exception ex) { ShowError("백업 위치가 올바르지 않습니다: " + ex.Message); return; }
            if (PathUtil.IsVirtual(path)) { ShowError("백업 위치는 PC 의 폴더(로컬·네트워크 드라이브)여야 합니다."); return; }
        }

        var still = _current.Where(p => !p.IsSelf && p.IsAlive).ToList();
        if (still.Count > 0 &&
            MessageBox.Show(this, $"아직 실행 중인 프로그램이 있습니다:\n{string.Join("\n", still.Select(s => s.Display))}\n\n그래도 계속할까요? 사용 중인 파일은 복사할 때 다시 묻습니다.",
                "백업 후 복사", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        BackupPath = path;
        _settings.BackupRoot = Compat.IsBlank(RootBox.Text) ? "backup" : RootBox.Text.Trim();
        _settings.BackupPrefix = PrefixBox.Text.Trim();
        _settings.BackupRestart = Restart;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
