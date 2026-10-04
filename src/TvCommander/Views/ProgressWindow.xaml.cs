using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TvCommander.IO;
using TvCommander.Model;

namespace TvCommander.Views;

public partial class ProgressWindow : Window
{
    private const int StallWarningMs = 5000;

    private readonly FileOperation _op;
    private readonly DispatcherTimer _timer;
    private readonly long _startTick = Environment.TickCount64;
    private bool _finished;

    public ProgressWindow(FileOperation op)
    {
        InitializeComponent();
        _op = op;
        Title = op.Title;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => UpdateView();
        _timer.Start();
        UpdateView();
    }

    private void UpdateView()
    {
        if (!_op.IsCancelled) PhaseText.Text = _op.Phase;
        FileText.Text = _op.CurrentFile;
        FileBar.Value = _op.CurrentSize > 0 ? (double)_op.CurrentDone / _op.CurrentSize : 0;
        TotalBar.Value = _op.TotalBytes > 0
            ? (double)_op.DoneBytes / _op.TotalBytes
            : _op.TotalFiles > 0 ? (double)_op.DoneFiles / _op.TotalFiles : 0;

        double secs = Math.Max(0.5, (Environment.TickCount64 - _startTick) / 1000.0);
        TotalText.Text = $"파일 {_op.DoneFiles:N0} / {_op.TotalFiles:N0},   {PathUtil.FormatSize(_op.DoneBytes)} / {PathUtil.FormatSize(_op.TotalBytes)},   {PathUtil.FormatSize((long)(_op.DoneBytes / secs))}/s"
                         + (_op.Errors + _op.Skipped > 0 ? $",   건너뜀 {_op.Errors + _op.Skipped}" : "");

        // 진행이 멈추면 경고 (네트워크 지연 등). 취소 버튼은 막힌 I/O 도 끊는다.
        long idle = Environment.TickCount64 - Interlocked.Read(ref _op.LastProgressTick);
        if (idle > StallWarningMs && !_op.IsCancelled)
        {
            StallText.Text = $"{idle / 1000}초째 응답이 없습니다. 네트워크나 디스크가 지연되고 있을 수 있습니다. [취소]를 누르면 중단합니다.";
            StallText.Visibility = Visibility.Visible;
        }
        else
        {
            StallText.Visibility = Visibility.Collapsed;
        }
    }

    public void Finish()
    {
        _finished = true;
        _timer.Stop();
        if (IsLoaded || IsVisible) Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs? e)
    {
        _op.Cancel();
        CancelButton.IsEnabled = false;
        PhaseText.Text = "취소하는 중...";
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
