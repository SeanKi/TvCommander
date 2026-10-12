using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MPCommander.IO;
using MPCommander.Model;
using MPCommander.Native;

namespace MPCommander.Views;

public sealed class SearchResult : System.ComponentModel.INotifyPropertyChanged
{
    private string _countText = "", _linesText = "";

    /// <summary>찾은 말 개수 ("…" 세는 중, "-" 셀 수 없음)</summary>
    public string CountText { get => _countText; set { _countText = value; Changed(nameof(CountText)); } }
    /// <summary>찾은 말이 있는 줄 수</summary>
    public string LinesText { get => _linesText; set { _linesText = value; Changed(nameof(LinesText)); } }
    public int Count { get; set; } = -1;
    public int Lines { get; set; } = -1;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

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

    // ── 결과마다 '찾은 개수 / 줄 수' 세기 (백그라운드, 뷰어와 같은 규칙) ──
    private const int CountWorkers = 4;
    private const int CountSizeLimit = 64 * 1024 * 1024;
    private readonly ConcurrentQueue<SearchResult> _countQueue = new();
    private readonly SemaphoreSlim _countSignal = new(0);
    private int _countGeneration;
    private string? _countFind;
    private bool _countCase;
    private System.Text.RegularExpressions.Regex? _countRegex;
    private long _totalCount, _totalLines;
    private int _counted, _countPending;

    public SearchWindow(MainWindow host, string root)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _host = host;
        RootBox.Text = root;
        ResultList.ItemsSource = _results;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Pump();
        Loaded += (_, _) => { MaskBox.Focus(); MaskBox.SelectAll(); };
        // Everything 이 떠 있을 때만 'Everything 사용' / '모든 드라이브'
        _everythingVersion = Everything.Version();
        if (_everythingVersion == null)
        {
            EverythingCheck.Visibility = Visibility.Collapsed;
            AllDrivesCheck.Visibility = Visibility.Collapsed;
        }
        else
        {
            EverythingCheck.Content = $"Everything {_everythingVersion}";
            EverythingCheck.IsChecked = _useEverything;
        }
        _uiReady = true;
        UpdateEverythingUi();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { if (_handle != IntPtr.Zero) Stop(); else Close(); e.Handled = true; }
        };
        Closed += (_, _) => { Interlocked.Increment(ref _countGeneration); Release(); };
    }

    public void SetRoot(string root)
    {
        if (_handle == IntPtr.Zero) RootBox.Text = root;
    }

    // ── Everything (voidtools) ──
    private const int EverythingMaxCandidates = 500_000;
    private static bool _useEverything = true;   // 창을 다시 열어도 기억 (프로그램 실행 동안)
    private readonly string? _everythingVersion;
    private bool _everythingListing;              // Everything 결과를 목록에 넣는 중 (이름만 찾기)
    private int _everythingCandidates = -1;       // 내용 찾기: Everything 이 준 후보 파일 수 (-1 = 폴더를 돎)
    private double _everythingSeconds;

    private bool EverythingOn => _everythingVersion != null && EverythingCheck.IsChecked == true;

    private void Everything_Click(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;   // InitializeComponent 중의 Checked 는 무시 (기억한 값을 덮지 않도록)
        _useEverything = EverythingCheck.IsChecked == true;
        UpdateEverythingUi();
    }

    private void AllDrives_Click(object sender, RoutedEventArgs e) => UpdateEverythingUi();

    private bool _uiReady;

    private void UpdateEverythingUi()
    {
        if (!_uiReady) return;
        AllDrivesCheck.IsEnabled = EverythingOn;
        if (!EverythingOn) AllDrivesCheck.IsChecked = false;
        RootBox.IsEnabled = AllDrivesCheck.IsChecked != true;
        // 숨김 여부는 Everything 이 색인 설정대로 준다 (속성을 묻는 것은 결과마다 디스크를 읽어 너무 느리다)
        HiddenCheck.IsEnabled = !EverythingOn;
        HiddenCheck.ToolTip = EverythingOn ? Loc.T("Everything 을 쓸 때는 Everything 의 색인 설정(숨김 파일 제외 등)을 따릅니다") : null;
    }

    /// <summary>
    /// Everything 으로 후보 목록을 얻는다. root 가 null 이면 모든 드라이브.
    /// Everything 이 없거나, 응답이 없거나, root 가 색인되지 않았으면 null (→ 폴더를 직접 돈다)
    /// </summary>
    private async Task<List<EverythingItem>?> QueryEverythingAsync(string? root, string mask, bool filesOnly)
    {
        if (root != null)
        {
            // 색인된 곳인지: 바로 아래 항목이 하나라도 나오는지 (빈 폴더면 직접 돌아도 금방이다)
            var probe = await Everything.QueryAsync("parent:" + Everything.Quote(root.Length > 3 ? root.TrimEnd('\\') : root), 1, 5000);
            if (probe is not { Count: > 0 }) return null;
        }
        var query = Everything.BuildQuery(root, RecursiveCheck.IsChecked == true, mask, filesOnly);
        return await Everything.QueryAsync(query, filesOnly ? EverythingMaxCandidates : MaxResults);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_handle != IntPtr.Zero) { Stop(); return; }
        if (_everythingListing) return;

        bool allDrives = EverythingOn && AllDrivesCheck.IsChecked == true;
        string? root = null;
        if (!allDrives)
        {
            try { root = PathUtil.Normalize(RootBox.Text); }
            catch (Exception ex) { StatusText.Text = ex.Message; return; }
        }
        var mask = NormalizeMask(MaskBox.Text);
        bool hasText = !Compat.IsEmpty(ContentBox.Text);

        // 정규식은 Everything 을 묻기 전에 확인한다
        if (RegexCheck.IsChecked == true && hasText)
        {
            try { TextRegex.Create(ContentBox.Text, CaseCheck.IsChecked == true, compiled: false); }
            catch (ArgumentException ex) { StatusText.Text = Loc.T("정규식 오류: ") + ex.Message; return; }
        }

        List<EverythingItem>? everything = null;
        _everythingCandidates = -1;
        if (EverythingOn && (root == null || !PathUtil.IsVirtual(root)))
        {
            StartButton.IsEnabled = false;
            StatusText.Text = Loc.T("Everything 에서 찾는 중...");
            long t0 = Compat.TickCount64;
            everything = await QueryEverythingAsync(root, mask, filesOnly: hasText);
            _everythingSeconds = (Compat.TickCount64 - t0) / 1000.0;
            StartButton.IsEnabled = true;
            if (everything == null && root == null) { StatusText.Text = Loc.T("Everything 이 응답하지 않습니다."); return; }
        }

        if (everything != null && !hasText)
        {
            // 이름만 찾기: Everything 결과가 곧 답이다
            _results.Clear();
            while (_pending.TryDequeue(out _)) { }
            StartCounting(null, false, null);
            foreach (var it in everything.Take(MaxResults))
                _pending.Enqueue(new SearchResult(it.FullPath, it.Size, it.LastWrite, it.IsFolder));
            _everythingListing = true;
            _startTick = Compat.TickCount64;
            _timer.Start();
            Pump();
            return;
        }
        if (everything != null) _everythingCandidates = everything.Count;

        string? host = null;
        if (everything == null && root != null)
        {
            StartButton.IsEnabled = false;
            StatusText.Text = Loc.T("연결 확인 중...");
            bool ok;
            (ok, host) = await NetworkHealth.CheckPathAsync(root);
            StartButton.IsEnabled = true;
            if (!ok) { StatusText.Text = new HostUnreachableException(host!).Message; return; }
        }

        // 정규식: 식이 맞는지 먼저 확인하고, 파일 내용 판정은 C++ 이 읽어 준 텍스트로 여기서 한다
        IntPtr textMatch = IntPtr.Zero;
        if (RegexCheck.IsChecked == true && !Compat.IsEmpty(ContentBox.Text))
        {
            System.Text.RegularExpressions.Regex re;
            try { re = TextRegex.Create(ContentBox.Text, CaseCheck.IsChecked == true, compiled: true); }
            catch (ArgumentException ex) { StatusText.Text = Loc.T("정규식 오류: ") + ex.Message; return; }
            var matcher = CreateTextMatcher(re);
            lock (KeepAlive) KeepAlive.Add(matcher);   // 멈춘 네이티브 스레드가 늦게 불러도 안전하도록 계속 붙잡아 둔다
            textMatch = Marshal.GetFunctionPointerForDelegate(matcher);
        }

        _results.Clear();
        while (_pending.TryDequeue(out _)) { }
        StartCounting(Compat.IsEmpty(ContentBox.Text) ? null : ContentBox.Text, CaseCheck.IsChecked == true,
            RegexCheck.IsChecked == true ? TextRegex.Create(ContentBox.Text, CaseCheck.IsChecked == true, compiled: true) : null);
        _callback = CreateCallback();
        // Everything 후보가 있으면 C++ 검색기는 폴더를 돌지 않고 그 파일들만 읽는다
        var files = everything == null ? IntPtr.Zero
            : Marshal.StringToHGlobalUni(string.Concat(everything.Select(i => i.FullPath + "\0")) + "\0");
        var p = new FmSearchParams
        {
            Files = files,
            Root = root ?? "",
            Mask = NormalizeMask(MaskBox.Text),
            Text = Compat.IsEmpty(ContentBox.Text) ? null : ContentBox.Text,
            CaseSensitive = CaseCheck.IsChecked == true ? 1 : 0,
            Recursive = RecursiveCheck.IsChecked == true ? 1 : 0,
            IncludeHidden = HiddenCheck.IsChecked == true ? 1 : 0,
            ThreadCount = host != null ? 8 : 4,
            IoTimeoutMs = IoTimeoutMs,
            TextMatch = textMatch,
        };
        _handle = NativeMethods.FmSearchStart(ref p, _callback, IntPtr.Zero);
        if (files != IntPtr.Zero) Marshal.FreeHGlobal(files);   // C++ 이 바로 복사해 둔다
        if (_handle == IntPtr.Zero) { StatusText.Text = Loc.T("검색을 시작하지 못했습니다."); return; }

        _startTick = Compat.TickCount64;
        StartButton.Content = Loc.T("중지");
        _timer.Start();
    }

    /// <summary>새 검색: 이전 세기는 버리고(세대 번호) 일꾼을 띄운다</summary>
    private void StartCounting(string? find, bool matchCase, System.Text.RegularExpressions.Regex? re)
    {
        int gen = Interlocked.Increment(ref _countGeneration);
        _doneSummaryBase = "";
        while (_countQueue.TryDequeue(out _)) { }
        _countFind = find;
        _countCase = matchCase;
        _countRegex = re;
        _totalCount = _totalLines = 0;
        _counted = _countPending = 0;
        if (find == null) return;
        for (int i = 0; i < CountWorkers; i++) _ = Task.Run(() => CountLoop(gen));
    }

    private async Task CountLoop(int gen)
    {
        while (gen == Volatile.Read(ref _countGeneration))
        {
            if (!await _countSignal.WaitAsync(1000)) { if (!IsLoaded && !IsVisible) return; continue; }
            if (gen != Volatile.Read(ref _countGeneration)) { _countSignal.Release(); return; }   // 다음 세대 몫은 돌려 준다
            if (!_countQueue.TryDequeue(out var r)) continue;
            string find = _countFind!;
            bool matchCase = _countCase;
            var re = _countRegex;
            (int Count, int Lines)? result = null;
            try
            {
                // 느린 네트워크에서도 멈추지 않도록 시간 제한 안에서 읽는다
                var data = await GuardedIo.RunAsync(ctx => ReadForCount(r.FullPath, ctx), 5000, r.FullPath);
                if (data != null) result = TextMatchCount.Count(TextMatchCount.Decode(data), find, matchCase, re);
            }
            catch { }
            if (gen != Volatile.Read(ref _countGeneration)) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (gen != _countGeneration) return;
                if (result is { } c)
                {
                    r.Count = c.Count;
                    r.Lines = c.Lines;
                    r.CountText = c.Count.ToString("N0");
                    r.LinesText = c.Lines.ToString("N0");
                    _totalCount += c.Count;
                    _totalLines += c.Lines;
                }
                else
                {
                    r.CountText = "-";
                }
                _counted++;
                _countPending--;
                if (_handle == IntPtr.Zero && _doneSummaryBase.Length > 0) RefreshDoneStatus();
            });
        }
    }

    /// <summary>개수 세기용으로 파일 전체를 읽는다 (너무 크면 null → '-')</summary>
    private static byte[]? ReadForCount(string path, IoContext ctx)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        if (fs.Length > CountSizeLimit) return null;
        var buf = new byte[fs.Length];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, Math.Min(256 * 1024, buf.Length - read));
            if (n == 0) break;
            read += n;
            ctx.Report();
            if (ctx.IsCancelled) return null;
        }
        if (read < buf.Length) Array.Resize(ref buf, read);
        return buf;
    }

    /// <summary>상태 줄의 '찾은 말 합계' 부분</summary>
    private string CountSummary()
    {
        if (_countFind == null) return "";
        var pending = _countPending > 0 ? Loc.F($" (세는 중 {_countPending:N0})") : "";
        return Loc.F($"   │   찾은 말 {_totalCount:N0}개 · {_totalLines:N0}줄{pending}");
    }

    private string _doneSummaryBase = "";

    private void RefreshDoneStatus() => StatusText.Text = Loc.T("완료 - ") + _doneSummaryBase.Replace("{COUNT}", CountSummary());

    /// <summary>"abc" → "*abc*" (와일드카드가 없으면 부분 일치)</summary>
    private static string NormalizeMask(string mask)
    {
        var parts = mask.SplitTrim(';')
                        .Select(p => p.IndexOfAny(['*', '?']) >= 0 ? p : $"*{p}*");
        var joined = string.Join(";", parts);
        return joined.Length == 0 ? "*" : joined;
    }

    private unsafe FmEntryBatchCallback CreateCallback() => OnHits;

    /// <summary>정규식 판정 콜백들 (검색이 끝나도 버리지 않는다 — 작은 객체라 괜찮다)</summary>
    private static readonly List<FmTextMatchCallback> KeepAlive = new();

    /// <summary>네이티브 검색 스레드에서 호출된다: 파일 내용 조각이 정규식에 맞으면 1</summary>
    private static unsafe FmTextMatchCallback CreateTextMatcher(System.Text.RegularExpressions.Regex re)
        => (text, length, _) =>
        {
            try { return re.IsMatch(new string(text, 0, length)) ? 1 : 0; }
            catch { return 0; }   // 시간 초과 등은 그 파일만 일치 아님
        };

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
            if (_countFind != null && !r.IsDirectory)
            {
                r.CountText = "…";
                Interlocked.Increment(ref _countPending);
                _countQueue.Enqueue(r);
                _countSignal.Release();
            }
        }
        if (_everythingListing)
        {
            // Everything 결과(이름만 찾기)를 목록에 다 넣으면 끝
            if (!_pending.IsEmpty) { StatusText.Text = Loc.F($"찾음 {_results.Count:N0}"); return; }
            bool capped = _results.Count >= MaxResults;
            while (_pending.TryDequeue(out _)) { }
            _doneSummaryBase = Loc.F($"찾음 {_results.Count:N0}{{COUNT}}") + Loc.F($"   │   Everything 으로 찾음 {_everythingSeconds:0.00}초")
                               + (capped ? Loc.T("   │   결과가 너무 많아 중단") : "");
            RefreshDoneStatus();
            _everythingListing = false;
            _timer.Stop();
            return;
        }
        if (_handle == IntPtr.Zero) return;

        char* cur = stackalloc char[512];
        NativeMethods.FmSearchGetStatus(_handle, out var st, cur, 512);
        double secs = (Compat.TickCount64 - _startTick) / 1000.0;
        string rest = _everythingCandidates >= 0
            // Everything 이 준 후보 파일을 읽는 중
            ? Loc.F($"   │   Everything 후보 {_everythingCandidates:N0} ({_everythingSeconds:0.00}초), 읽음 {st.FilesScanned:N0}   │   오류/시간초과 {st.Errors:N0}   │   {secs:0.0}초")
            : Loc.F($"   │   폴더 {st.DirsScanned:N0}, 파일 {st.FilesScanned:N0}   │   오류/시간초과 {st.Errors:N0}   │   {secs:0.0}초");
        string summary = Loc.F($"찾음 {_results.Count:N0}{CountSummary()}") + rest;

        if (_results.Count >= MaxResults)
        {
            NativeMethods.FmSearchCancel(_handle);
            summary += Loc.T("   │   결과가 너무 많아 중단");
        }

        if (st.Running != 0)
        {
            StatusText.Text = summary + $"   │   {new string(cur)}";
        }
        else if (_pending.IsEmpty)
        {
            _doneSummaryBase = Loc.F($"찾음 {_results.Count:N0}{{COUNT}}") + rest + (_results.Count >= MaxResults ? Loc.T("   │   결과가 너무 많아 중단") : "");
            RefreshDoneStatus();   // 검색이 끝난 뒤에도 개수 세기가 끝날 때까지 합계를 고쳐 쓴다
            Release();
        }
    }

    private void Stop()
    {
        if (_handle != IntPtr.Zero) NativeMethods.FmSearchCancel(_handle);
        StatusText.Text = Loc.T("중지하는 중...");
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
        StartButton.Content = Loc.T("찾기");
    }

    private void GoTo_Click(object sender, RoutedEventArgs e) => GoToSelected();

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => GoToSelected();

    private void ResultList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { GoToSelected(); e.Handled = true; }
        else if (e.Key == Key.F3 && ResultList.SelectedItem is SearchResult { IsDirectory: false } r)
        {
            // '포함 텍스트'로 찾은 파일이면 뷰어에서 그 말을 바로 찾는다 (F3 다음, 아래에 찾은 줄 목록)
            var find = Compat.IsEmpty(ContentBox.Text) ? null : ContentBox.Text;
            new ViewerWindow(r.FullPath, find, CaseCheck.IsChecked == true, RegexCheck.IsChecked == true) { Owner = this }.Show();
            e.Handled = true;
        }
    }

    private void GoToSelected()
    {
        if (ResultList.SelectedItem is SearchResult r) _host.GoToFile(r.FullPath);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
