using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using TvCommander.IO;
using TvCommander.Model;
using TvCommander.Native;

namespace TvCommander.Views;

public sealed class SearchResult
{
    public SearchResult(string fullPath, long size, long lastWrite, bool isDirectory)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        Folder = Path.GetDirectoryName(fullPath) ?? "";
        SizeText = isDirectory ? "<DIR>" : size.ToString("N0");
        DateText = lastWrite > 0 ? DateTime.FromFileTimeUtc(lastWrite).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
        IsDirectory = isDirectory;
    }

    public string FullPath { get; }
    public string Name { get; }
    public string Folder { get; }
    public string SizeText { get; }
    public string DateText { get; }
    public bool IsDirectory { get; }
}

/// <summary>Alt+F7 파일 찾기. 탐색은 C++ 멀티스레드 검색기가 하고, 막힌 I/O 는 네이티브 워치독이 끊는다.</summary>
public partial class SearchWindow : Window
{
    private const int MaxResults = 200_000;
    private const int IoTimeoutMs = 5000;

    private readonly MainWindow _host;
    private readonly ObservableCollection<SearchResult> _results = new();
    private readonly ConcurrentQueue<SearchResult> _pending = new();
    private readonly DispatcherTimer _timer;
    private IntPtr _handle;
    private FmEntryBatchCallback? _callback;
    private long _startTick;

    public SearchWindow(MainWindow host, string root)
    {
        InitializeComponent();
        _host = host;
        RootBox.Text = root;
        ResultList.ItemsSource = _results;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Pump();
        Loaded += (_, _) => { MaskBox.Focus(); MaskBox.SelectAll(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (_handle != IntPtr.Zero) Stop(); else Close(); e.Handled = true; }
        };
        Closed += (_, _) => Release();
    }

    public void SetRoot(string root)
    {
        if (_handle == IntPtr.Zero) RootBox.Text = root;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_handle != IntPtr.Zero) { Stop(); return; }

        string root;
        try { root = PathUtil.Normalize(RootBox.Text); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }

        StartButton.IsEnabled = false;
        StatusText.Text = "연결 확인 중...";
        var (ok, host) = await NetworkHealth.CheckPathAsync(root);
        StartButton.IsEnabled = true;
        if (!ok) { StatusText.Text = new HostUnreachableException(host!).Message; return; }

        _results.Clear();
        _pending.Clear();
        _callback = CreateCallback();
        var p = new FmSearchParams
        {
            Root = root,
            Mask = NormalizeMask(MaskBox.Text),
            Text = string.IsNullOrEmpty(ContentBox.Text) ? null : ContentBox.Text,
            CaseSensitive = CaseCheck.IsChecked == true ? 1 : 0,
            Recursive = RecursiveCheck.IsChecked == true ? 1 : 0,
            IncludeHidden = HiddenCheck.IsChecked == true ? 1 : 0,
            ThreadCount = host != null ? 8 : 4,
            IoTimeoutMs = IoTimeoutMs,
        };
        _handle = NativeMethods.FmSearchStart(ref p, _callback, IntPtr.Zero);
        if (_handle == IntPtr.Zero) { StatusText.Text = "검색을 시작하지 못했습니다."; return; }

        _startTick = Environment.TickCount64;
        StartButton.Content = "중지";
        _timer.Start();
    }

    /// <summary>"abc" → "*abc*" (와일드카드가 없으면 부분 일치)</summary>
    private static string NormalizeMask(string mask)
    {
        var parts = mask.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.IndexOfAny(['*', '?']) >= 0 ? p : $"*{p}*");
        var joined = string.Join(";", parts);
        return joined.Length == 0 ? "*" : joined;
    }

    private unsafe FmEntryBatchCallback CreateCallback() => OnHits;

    /// <summary>네이티브 스레드에서 호출된다.</summary>
    private unsafe int OnHits(FmEntry* entries, int count, IntPtr user)
    {
        for (int i = 0; i < count; i++)
        {
            ref FmEntry e = ref entries[i];
            _pending.Enqueue(new SearchResult(Marshal.PtrToStringUni(e.Name)!, (long)e.Size, (long)e.LastWrite,
                (e.Attributes & (uint)FileAttributes.Directory) != 0));
        }
        return 1;
    }

    private unsafe void Pump()
    {
        int added = 0;
        while (added < 5000 && _results.Count < MaxResults && _pending.TryDequeue(out var r))
        {
            _results.Add(r);
            added++;
        }
        if (_handle == IntPtr.Zero) return;

        char* cur = stackalloc char[512];
        NativeMethods.FmSearchGetStatus(_handle, out var st, cur, 512);
        double secs = (Environment.TickCount64 - _startTick) / 1000.0;
        string summary = $"찾음 {_results.Count:N0}   │   폴더 {st.DirsScanned:N0}, 파일 {st.FilesScanned:N0}   │   오류/시간초과 {st.Errors:N0}   │   {secs:0.0}초";

        if (_results.Count >= MaxResults)
        {
            NativeMethods.FmSearchCancel(_handle);
            summary += "   │   결과가 너무 많아 중단";
        }

        if (st.Running != 0)
        {
            StatusText.Text = summary + $"   │   {new string(cur)}";
        }
        else if (_pending.IsEmpty)
        {
            StatusText.Text = "완료 - " + summary;
            Release();
        }
    }

    private void Stop()
    {
        if (_handle != IntPtr.Zero) NativeMethods.FmSearchCancel(_handle);
        StatusText.Text = "중지하는 중...";
    }

    private void Release()
    {
        _timer.Stop();
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.FmSearchFree(_handle);   // 이후 콜백 없음
            _handle = IntPtr.Zero;
        }
        _callback = null;
        StartButton.Content = "찾기";
    }

    private void GoTo_Click(object sender, RoutedEventArgs e) => GoToSelected();

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => GoToSelected();

    private void ResultList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { GoToSelected(); e.Handled = true; }
        else if (e.Key == Key.F3 && ResultList.SelectedItem is SearchResult { IsDirectory: false } r)
        {
            new ViewerWindow(r.FullPath) { Owner = this }.Show();
            e.Handled = true;
        }
    }

    private void GoToSelected()
    {
        if (ResultList.SelectedItem is SearchResult r) _host.GoToFile(r.FullPath);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
