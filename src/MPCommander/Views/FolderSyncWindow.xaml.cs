using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 폴더 비교 · 동기화 (Shift+F2): 활성 패널 폴더(A)와 대상 패널 폴더(B)를 하위 폴더까지 비교하고,
/// 양쪽 똑같이 / 한쪽 → 반대쪽 최신만 / 한쪽만 남기기 로 맞춘다. 어느 쪽을 남길지 조언한다.
/// </summary>
public partial class FolderSyncWindow : Window
{
    private readonly MainWindow _host;
    private FolderCompare? _result;
    private CancellationTokenSource? _cts;
    private bool _modeFromAdvice = true;   // 첫 비교에는 조언한 방식을 고른다

    public FolderSyncWindow(MainWindow host, string a, string b)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _host = host;
        PathA.Text = a;
        PathB.Text = b;
        ExcludeBox.Text = ".git;.vs";
        ModeBoth.IsChecked = true;
        Loaded += async (_, _) => await CompareAsync();
        Closed += (_, _) => _cts?.Cancel();
    }

    private SyncMode Mode =>
        ModeAtoB.IsChecked == true ? SyncMode.AtoB :
        ModeBtoA.IsChecked == true ? SyncMode.BtoA :
        ModeMirrorA.IsChecked == true ? SyncMode.MirrorA :
        ModeMirrorB.IsChecked == true ? SyncMode.MirrorB : SyncMode.Both;

    private void SetMode(SyncMode m)
    {
        (m switch
        {
            SyncMode.AtoB => ModeAtoB,
            SyncMode.BtoA => ModeBtoA,
            SyncMode.MirrorA => ModeMirrorA,
            SyncMode.MirrorB => ModeMirrorB,
            _ => ModeBoth,
        }).IsChecked = true;
    }

    // ───────────────────────── 비교 ─────────────────────────

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); return; }   // 비교 중이면 중지
        await CompareAsync();
    }

    private void SwapAB_Click(object sender, RoutedEventArgs e)
    {
        (PathA.Text, PathB.Text) = (PathB.Text, PathA.Text);
        _ = CompareAsync();
    }

    private async Task CompareAsync()
    {
        string a, b;
        try { a = PathUtil.Normalize(PathA.Text); b = PathUtil.Normalize(PathB.Text); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        if (PathUtil.IsVirtual(a) || PathUtil.IsVirtual(b)) { StatusText.Text = Loc.T("PC 폴더(로컬·네트워크 드라이브)끼리만 비교합니다."); return; }
        if (PathUtil.Same(a, b)) { StatusText.Text = Loc.T("A 와 B 가 같은 폴더입니다."); return; }
        if (PathUtil.IsUnder(a, b) || PathUtil.IsUnder(b, a)) { StatusText.Text = Loc.T("한 폴더가 다른 폴더 안에 있습니다. 서로 떨어진 두 폴더를 고르세요."); return; }

        foreach (var p in new[] { a, b })
        {
            var (ok, host) = await NetworkHealth.CheckPathAsync(p);
            if (!ok) { StatusText.Text = new HostUnreachableException(host!).Message; return; }
        }

        var excludes = ExcludeBox.Text.SplitTrim(';');
        bool content = ContentCheck.IsChecked == true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        CompareButton.Content = Loc.T("중지");
        RunButton.IsEnabled = false;
        List.ItemsSource = null;
        AdviceBox.Visibility = Visibility.Collapsed;
        StatusText.Text = Loc.T("비교 중...");
        long t0 = Compat.TickCount64;
        try
        {
            // 폴더 읽기·비교는 작업 스레드에서 (진행은 상태 줄로)
            var r = await Task.Run(() => FolderCompare.Run(a, b, excludes, content,
                msg => Dispatcher.BeginInvoke(() => { if (!ct.IsCancellationRequested) StatusText.Text = msg; }), ct), ct);
            _result = r;
            var (advice, suggest) = r.Advice();
            AdviceText.Text = advice;
            AdviceBox.Visibility = Visibility.Visible;
            if (_modeFromAdvice) { _modeFromAdvice = false; SetMode(suggest); }
            ApplyPlan();
            double secs = (Compat.TickCount64 - t0) / 1000.0;
            var extra = r.ContentSame > 0 ? Loc.F($", 내용이 같아 같은 파일로 본 것 {r.ContentSame:N0}") : "";
            var errs = r.Errors.Count > 0 ? Loc.F($"   │   읽지 못한 폴더 {r.Errors.Count:N0}") : "";
            _compareSummary = Loc.F($"비교 {secs:0.0}초 — 다른 항목 {r.Entries.Count:N0}, 같은 파일 {r.Same:N0}{extra}") + errs;
            UpdateCounts();
        }
        catch (OperationCanceledException) { StatusText.Text = Loc.T("비교를 중지했습니다."); }
        catch (Exception ex) { StatusText.Text = Loc.T("비교하지 못했습니다: ") + ex.Message; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            CompareButton.Content = Loc.T("비교");
        }
    }

    private string _compareSummary = "";

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_result == null || !IsLoaded) return;
        ApplyPlan();
    }

    private void ApplyPlan()
    {
        if (_result == null) return;
        _result.Plan(Mode);
        foreach (var en in _result.Entries)
        {
            en.IncludeChanged -= UpdateCounts;
            en.IncludeChanged += UpdateCounts;
        }
        RefreshList();
        UpdateCounts();
    }

    private void ShowSkipped_Click(object sender, RoutedEventArgs e) => RefreshList();

    private void RefreshList()
    {
        if (_result == null) return;
        List.ItemsSource = ShowSkipped.IsChecked == true ? _result.Entries : _result.Entries.Where(x => x.Action != SyncAct.None).ToList();
    }

    private void UpdateCounts()
    {
        if (_result == null) return;
        var inc = _result.Entries.Where(x => x.Include && x.Action != SyncAct.None).ToList();
        long ab = inc.Where(x => x.Action == SyncAct.CopyAtoB).Sum(x => x.A!.Size);
        long ba = inc.Where(x => x.Action == SyncAct.CopyBtoA).Sum(x => x.B!.Size);
        int nab = inc.Count(x => x.Action == SyncAct.CopyAtoB), nba = inc.Count(x => x.Action == SyncAct.CopyBtoA);
        int del = inc.Count(x => x.Action is SyncAct.DeleteA or SyncAct.DeleteB);
        StatusText.Text = _compareSummary + Loc.F($"   │   실행: A → B {nab:N0} ({PathUtil.FormatSize(ab)}), A ← B {nba:N0} ({PathUtil.FormatSize(ba)}), 삭제 {del:N0}");
        RunButton.IsEnabled = inc.Count > 0;
    }

    // ───────────────────────── 목록 ─────────────────────────

    private List<SyncEntry> Selected() => List.SelectedItems.OfType<SyncEntry>().ToList();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        e.Handled = true;
        var sel = Selected();
        if (sel.Count == 0) return;
        bool on = !sel[0].Include;
        foreach (var s in sel) s.Include = on && s.Action != SyncAct.None;
    }

    private void SetAction(SyncAct act)
    {
        foreach (var s in Selected())
        {
            if (act == SyncAct.CopyAtoB && s.A == null) continue;   // 없는 쪽에서는 복사할 수 없다
            if (act == SyncAct.CopyBtoA && s.B == null) continue;
            s.Note = "";
            s.Action = act;
            s.Include = act != SyncAct.None;
        }
        UpdateCounts();
    }

    private void SetAtoB_Click(object sender, RoutedEventArgs e) => SetAction(SyncAct.CopyAtoB);
    private void SetBtoA_Click(object sender, RoutedEventArgs e) => SetAction(SyncAct.CopyBtoA);
    private void SetNone_Click(object sender, RoutedEventArgs e) => SetAction(SyncAct.None);

    private void ShowIn(bool a)
    {
        if (_result == null || List.SelectedItem is not SyncEntry s) return;
        var root = a ? _result.A : _result.B;
        var path = Path.Combine(root, s.Relative);
        if (!File.Exists(path) && !Directory.Exists(path)) path = Path.GetDirectoryName(path) ?? root;   // 이쪽에는 없으면 그 폴더
        _host.GoToFile(path);
    }

    private void ShowInA_Click(object sender, RoutedEventArgs e) => ShowIn(true);
    private void ShowInB_Click(object sender, RoutedEventArgs e) => ShowIn(false);
    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is SyncEntry s) ShowIn(s.A != null);
    }

    // ───────────────────────── 실행 ─────────────────────────

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        var inc = _result.Entries.Where(x => x.Include && x.Action != SyncAct.None).ToList();
        if (inc.Count == 0) return;
        int del = inc.Count(x => x.Action is SyncAct.DeleteA or SyncAct.DeleteB);
        int overwriteNewer = inc.Count(x => (x.Action == SyncAct.CopyAtoB && x.Diff == SyncDiff.NewerB) || (x.Action == SyncAct.CopyBtoA && x.Diff == SyncDiff.NewerA));
        var msg = Loc.F($"{inc.Count:N0}개 항목을 동기화합니다.\n\nA: {_result.A}\nB: {_result.B}");
        if (del > 0)
        {
            msg += Loc.F($"\n\n{del:N0}개 항목을 지웁니다 (휴지통으로).");
            if (_result.A.StartsWith(@"\\") || _result.B.StartsWith(@"\\") || IsNetworkDrive(_result.A) || IsNetworkDrive(_result.B))
                msg += Loc.T("\n네트워크 폴더에는 휴지통이 없어 바로 지워집니다.");
        }
        if (overwriteNewer > 0) msg += Loc.F($"\n\n더 최신 파일 {overwriteNewer:N0}개를 오래된 파일로 덮어씁니다.");
        if (MessageBox.Show(this, msg, Title, MessageBoxButton.OKCancel,
                del > 0 || overwriteNewer > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;

        var op = new FolderSyncOperation(_result.A, _result.B, inc);
        RunButton.IsEnabled = false;
        await _host.RunOperationAsync(op);
        await _host.RefreshPanelsUnderAsync(_result.A, _result.B);
        var done = Loc.F($"동기화: 복사 {op.Copied:N0}, 삭제 {op.Deleted:N0}") +
                   (op.Errors > 0 ? Loc.F($", 실패 {op.Errors:N0}") : "") + (op.IsCancelled ? Loc.T(" — 중간에 취소됨") : "");
        await CompareAsync();   // 다시 비교해 남은 차이를 보여 준다
        StatusText.Text = done + "   │   " + StatusText.Text;
    }

    private static bool IsNetworkDrive(string path)
    {
        try { return path.Length >= 2 && path[1] == ':' && new DriveInfo(path.Substring(0, 2)).DriveType == DriveType.Network; }
        catch { return false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
