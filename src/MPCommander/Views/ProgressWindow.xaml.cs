using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

public partial class ProgressWindow : Window
{
    private const int StallWarningMs = 5000;
    private const int MaxLogLines = 5000;   // 창에는 최근 것만 (전체는 로그 파일에)

    /// <summary>상세 기록 보기 (설정 ShowOperationLog). 바꾸면 DetailsChanged 로 저장</summary>
    public static bool ShowDetails { get; set; } = true;
    public static Action<bool>? DetailsChanged { get; set; }

    private readonly ProgressOperation _op;
    private readonly DispatcherTimer _timer;
    private readonly long _startTick = Compat.TickCount64;
    private ObservableCollection<string> _lines = new();
    private bool _finished;

    public ProgressWindow(ProgressOperation op)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _op = op;
        Title = op.Title;
        LogList.ItemsSource = _lines;
        if (op.Log == null)
        {
            DetailsCheck.Visibility = Visibility.Collapsed;
            LogFolderButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailsCheck.IsChecked = ShowDetails;
            LogList.Visibility = ShowDetails ? Visibility.Visible : Visibility.Collapsed;
        }
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => UpdateView();
        _timer.Start();
        UpdateView();
    }

    private bool DetailsOn => _op.Log != null && DetailsCheck.IsChecked == true;

    private void UpdateView()
    {
        if (!_op.IsCancelled && !_finished) PhaseText.Text = _op.Phase;
        if (!_finished) FileText.Text = _op.CurrentFile;
        FileBar.Value = _op.CurrentSize > 0 ? (double)_op.CurrentDone / _op.CurrentSize : 0;
        TotalBar.Value = _op.TotalBytes > 0
            ? (double)_op.DoneBytes / _op.TotalBytes
            : _op.TotalFiles > 0 ? (double)_op.DoneFiles / _op.TotalFiles : 0;

        double secs = Math.Max(0.5, (Compat.TickCount64 - _startTick) / 1000.0);
        TotalText.Text = Loc.F($"파일 {_op.DoneFiles:N0} / {_op.TotalFiles:N0},   {PathUtil.FormatSize(_op.DoneBytes)} / {PathUtil.FormatSize(_op.TotalBytes)},   {PathUtil.FormatSize((long)(_op.DoneBytes / secs))}/s")
                         + (_op.Errors + _op.Skipped > 0 ? Loc.F($",   건너뜀 {_op.Errors + _op.Skipped}") : "");

        AppendLog();

        // 진행이 멈추면 경고 (네트워크 지연 등). 취소 버튼은 막힌 I/O 도 끊는다.
        long idle = Compat.TickCount64 - Interlocked.Read(ref _op.LastProgressTick);
        if (idle > StallWarningMs && !_op.IsCancelled && !_op.WaitingForUser && !_finished)
        {
            StallText.Text = Loc.F($"{idle / 1000}초째 응답이 없습니다. 네트워크나 디스크가 지연되고 있을 수 있습니다. [취소]를 누르면 중단합니다.");
            StallText.Visibility = Visibility.Visible;
        }
        else
        {
            StallText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>새 기록 줄을 목록 끝에 붙이고 끝으로 굴린다 (너무 많으면 앞쪽을 버린다)</summary>
    private void AppendLog()
    {
        if (_op.Log == null) return;
        var lines = _op.Log.Drain();
        if (lines.Count == 0) return;
        if (_lines.Count + lines.Count > MaxLogLines)
        {
            var keep = _lines.Concat(lines).Skip(_lines.Count + lines.Count - MaxLogLines * 3 / 5);
            _lines = new ObservableCollection<string>(keep);
            LogList.ItemsSource = _lines;
        }
        else
        {
            foreach (var l in lines) _lines.Add(l);
        }
        if (DetailsOn) ScrollLogToEnd();
    }

    /// <summary>맨 아래 줄로 — 세로로만 (ScrollIntoView 는 긴 줄이면 가로로도 밀어 시각이 잘린다)</summary>
    private void ScrollLogToEnd()
    {
        _logScroll ??= FindScroll(LogList);
        _logScroll?.ScrollToBottom();
    }

    private System.Windows.Controls.ScrollViewer? _logScroll;

    private static System.Windows.Controls.ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is System.Windows.Controls.ScrollViewer sv) return sv;
            if (FindScroll(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>작업이 끝남. 상세 기록을 보고 있으면 결과를 볼 수 있게 창을 남긴다</summary>
    public void Finish()
    {
        _finished = true;
        _timer.Stop();
        if (DetailsOn && IsVisible)
        {
            UpdateView();
            PhaseText.Text = _op.IsCancelled ? Loc.T("취소했습니다") : _op.Errors + _op.Skipped > 0 ? Loc.T("끝났습니다 (건너뛴 항목이 있습니다)") : Loc.T("끝났습니다");
            FileText.Text = "";
            FileBar.Value = 0;
            CancelButton.Content = Loc.T("닫기");
            CancelButton.IsEnabled = true;
            CancelButton.Focus();
            return;
        }
        if (IsLoaded || IsVisible) Close();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        ShowDetails = DetailsCheck.IsChecked == true;
        LogList.Visibility = ShowDetails ? Visibility.Visible : Visibility.Collapsed;
        if (ShowDetails) Dispatcher.BeginInvoke(ScrollLogToEnd, DispatcherPriority.Loaded);
        DetailsChanged?.Invoke(ShowDetails);
        if (_finished && !ShowDetails) Close();
    }

    private void LogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(OperationLog.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OperationLog.Folder}\"") { UseShellExecute = true });
        }
        catch { /* 탐색기를 못 열어도 그만 */ }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs? e)
    {
        if (_finished) { Close(); return; }
        _op.Cancel();
        CancelButton.IsEnabled = false;
        PhaseText.Text = Loc.T("취소하는 중...");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            Cancel_Click(null, null);
        }
        base.OnClosing(e);
    }
}
