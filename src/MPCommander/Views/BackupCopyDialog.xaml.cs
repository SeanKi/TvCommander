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

    private readonly string? _keepPath;   // 복원: 지금 파일을 옮겨 둘 곳 (backup\last_one)

    public string BackupPath { get; private set; } = "";
    public bool Restart => RestartCheck.IsChecked == true;
    /// <summary>[방향 바꾸기] 를 눌러 닫았음 — 원본·대상 패널을 바꿔 다시 연다</summary>
    public bool SwapRequested { get; private set; }
    /// <summary>한 번이라도 대상 파일을 쓰고 있던 프로그램 (복사 뒤 다시 실행 후보)</summary>
    public IReadOnlyCollection<LockingProcess> Seen => _seen.Values;

    private BackupCopyDialog(AppSettings settings, BackupCopyPlan plan, string sourceText, string? keepPath)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _settings = settings;
        _plan = plan;
        _keepPath = keepPath;

        SourceText.Text = sourceText;
        TargetText.Text = plan.TargetDirectory;
        int over = plan.Overwrites.Count();
        CountText.Text = Loc.F($"{plan.Items.Count:N0}개 ({PathUtil.FormatSize(plan.TotalBytes)}) — 덮어쓸 파일 {over:N0}개는 먼저 백업, 새 파일 {plan.Items.Count - over:N0}개");
        RootBox.Text = settings.BackupRoot;
        PrefixBox.Text = settings.BackupPrefix;
        RestartCheck.IsChecked = settings.BackupRestart;

        if (keepPath != null)
        {
            // 복원: 백업해 둔 파일을 대상으로 되돌리고, 그때 새로 생긴 파일은 치운다. 지금 파일은 last_one 에 보관
            Title = Loc.T("백업에서 복원 (Restore)");
            IntroText.Text = Loc.T("백업 폴더의 기록(backup-copy.log)을 보고 대상 폴더를 백업 전으로 되돌립니다. 지금 대상에 있는 파일은 지우지 않고 아래 보관 폴더로 옮겨 둡니다 (그 폴더에서 다시 복원할 수 있습니다).");
            SourceLabel.Text = Loc.T("백업");
            CountText.Text = Loc.F($"되돌릴 파일 {plan.Items.Count:N0}개 ({PathUtil.FormatSize(plan.TotalBytes)}), 그때 새로 생긴 파일 {plan.Removals.Count:N0}개는 치움");
            BackupFields.Visibility = Visibility.Collapsed;
            SwapButton.Visibility = Visibility.Collapsed;
            OkButton.Content = Loc.T("계속 (복원)");
        }
        UpdatePreview();
        Loaded += async (_, _) => await CheckAsync();
    }

    /// <summary>계속을 누르면 대화상자, 취소하면 null. [방향 바꾸기] 면 SwapRequested 인 대화상자</summary>
    public static BackupCopyDialog? Show(Window owner, AppSettings settings, BackupCopyPlan plan, string sourceText)
    {
        var dlg = new BackupCopyDialog(settings, plan, sourceText, null) { Owner = owner };
        return dlg.ShowDialog() == true || dlg.SwapRequested ? dlg : null;
    }

    /// <summary>복원 준비 창. keepPath = 지금 파일을 옮겨 둘 곳</summary>
    public static BackupCopyDialog? ShowRestore(Window owner, AppSettings settings, BackupCopyPlan plan, string keepPath)
    {
        var dlg = new BackupCopyDialog(settings, plan, plan.RestoreFrom ?? "", keepPath) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg : null;
    }

    private void Swap_Click(object sender, RoutedEventArgs e)
    {
        SwapRequested = true;
        Close();
    }

    private void Backup_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (BackupPreview == null || PrefixBox == null) return;
        if (_keepPath != null)
        {
            BackupPreview.Text = Loc.F($"지금 파일 보관: {_keepPath}\\");
            return;
        }
        if (!_plan.Overwrites.Any())
        {
            BackupPreview.Text = Loc.T("덮어쓸 파일이 없어 백업 폴더는 만들지 않습니다.");
            return;
        }
        try
        {
            BackupPreview.Text = Loc.F($"백업 폴더: {_plan.MakeBackupPath(RootBox.Text.Trim(), PrefixBox.Text, _now)}\\");
        }
        catch (Exception ex)
        {
            BackupPreview.Text = Loc.T("백업 위치가 올바르지 않습니다: ") + ex.Message;
        }
    }

    // ───────────────────────── 실행 중인 프로그램 ─────────────────────────

    private async Task CheckAsync()
    {
        if (_checking) return;
        _checking = true;
        SetBusy(true, Loc.T("대상 파일을 쓰고 있는 프로그램을 찾는 중..."));
        try
        {
            // 덮어쓸 파일만 확인하면 된다 (새로 생기는 파일은 아무도 쓰지 않는다)
            var files = _plan.Overwrites.Select(o => o.Destination)
                .Concat(_plan.Removals.Select(r => Path.Combine(_plan.TargetDirectory, r))).ToList();
            var find = Task.Run(() => RunningProcesses.FindUsing(files));
            if (await Task.WhenAny(find, Task.Delay(15000)) != find)
            {
                SetBusy(false, Loc.T("확인이 15초 안에 끝나지 않았습니다. 그대로 계속할 수 있습니다 (사용 중인 파일은 복사 중에 묻습니다)."));
                return;
            }
            _current = await find;
            foreach (var p in _current) _seen[p.Pid] = p;
            ProcessList.ItemsSource = _current;

            var others = _current.Where(p => !p.IsSelf).ToList();
            string msg = others.Count == 0
                ? (_keepPath != null
                    ? Loc.T("대상 파일을 쓰고 있는 프로그램이 없습니다. [계속]을 누르면 복원합니다.")
                    : Loc.T("대상 파일을 쓰고 있는 프로그램이 없습니다. [계속]을 누르면 백업 후 복사합니다."))
                : Loc.F($"{others.Count}개 프로그램이 대상 파일을 쓰고 있습니다. 프로그램을 저장·종료한 뒤(또는 [닫기 요청]) [다시 확인] 하고 [계속]을 누르세요.");
            if (_current.Any(p => p.IsSelf))
                msg += Loc.T("\nMP-Commander 자신이 쓰는 파일은 복사 중에 '이름 바꿔 교체'를 고르면 바꿀 수 있습니다 (다시 시작하면 적용).");
            SetBusy(false, msg);
        }
        catch (Exception ex)
        {
            SetBusy(false, Loc.T("프로그램 확인 실패: ") + ex.Message + Loc.T(" — 그대로 계속할 수 있습니다."));
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
        SetBusy(true, Loc.T("닫기를 요청하는 중... (저장 확인이 뜨면 그 프로그램에서 답하세요)"));
        var failed = await Task.Run(() => targets.Where(p => !RunningProcesses.CloseGracefully(p, 10000)).ToList());
        await CheckAsync();
        if (failed.Count > 0)
            ProcessStatus.Text += Loc.F($"\n닫히지 않은 프로그램: {string.Join(", ", failed.Select(f => f.Name))} — 직접 닫거나 [강제 종료] 하세요.");
    }

    private async void KillProcesses_Click(object sender, RoutedEventArgs e)
    {
        var targets = Chosen();
        if (targets.Count == 0) return;
        if (MessageBox.Show(this, Loc.F($"다음 프로그램을 강제로 끝냅니다. 저장하지 않은 내용은 사라집니다.\n\n{string.Join("\n", targets.Select(t => $"{t.Display}  (PID {t.Pid})"))}"),
                Loc.T("강제 종료"), MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        SetBusy(true, Loc.T("강제 종료하는 중..."));
        var failed = await Task.Run(() => targets.Where(p => !RunningProcesses.Kill(p)).ToList());
        await CheckAsync();
        if (failed.Count > 0)
            ProcessStatus.Text += Loc.F($"\n끝내지 못한 프로그램: {string.Join(", ", failed.Select(f => f.Name))} (관리자 권한으로 실행된 프로그램일 수 있습니다)");
    }

    // ───────────────────────── 확인 ─────────────────────────

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string path = "";
        if (_keepPath != null) path = _keepPath;
        else if (_plan.Overwrites.Any())
        {
            try { path = _plan.MakeBackupPath(RootBox.Text.Trim(), PrefixBox.Text, _now); }
            catch (Exception ex) { ShowError(Loc.T("백업 위치가 올바르지 않습니다: ") + ex.Message); return; }
            if (PathUtil.IsVirtual(path)) { ShowError(Loc.T("백업 위치는 PC 의 폴더(로컬·네트워크 드라이브)여야 합니다.")); return; }
        }

        var still = _current.Where(p => !p.IsSelf && p.IsAlive).ToList();
        if (still.Count > 0 &&
            MessageBox.Show(this, Loc.F($"아직 실행 중인 프로그램이 있습니다:\n{string.Join("\n", still.Select(s => s.Display))}\n\n그래도 계속할까요? 사용 중인 파일은 복사할 때 다시 묻습니다."),
                Loc.T("백업 후 복사"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        BackupPath = path;
        if (_keepPath == null)
        {
            _settings.BackupRoot = Compat.IsBlank(RootBox.Text) ? "backup" : RootBox.Text.Trim();
            _settings.BackupPrefix = PrefixBox.Text.Trim();
        }
        _settings.BackupRestart = Restart;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
