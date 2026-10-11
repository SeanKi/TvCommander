using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// F3 내장 뷰어. 텍스트는 보이는 줄만 그리는 가상 보기(VirtualTextView)라 수 GB 파일도 바로 열린다.
///  - 줄 색인·임의 위치 읽기·일반 문자열 찾기는 C++(FmText), 정규식 찾기는 C# 여러 스레드(LargeTextSearch).
///  - 찾기는 늘 파일 전체. 찾은 줄은 아래 목록에 '덧붙이기만' 하므로(선택·스크롤 유지) 깜박이지 않는다.
///  - 16진수(H)는 앞 512 KB, 이미지는 그대로.
/// </summary>
public partial class ViewerWindow : Window
{
    private const int HexLimit = 512 * 1024;
    private const int ImageLimit = 64 * 1024 * 1024;
    private const int MaxListLines = 200_000;     // 목록에 올리는 찾은 줄 최대 (개수는 그 뒤로도 센다)
    private const int PreviewChars = 400;
    private const int MaxCopyChars = 64 * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };

    private readonly string _path;
    private readonly bool _isImage;
    private TextFileSource? _source;
    private bool _hex;
    private string _encodingName = "";
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _findTimer;
    private bool _syncing;

    // ── 찾기 상태 ──
    private int _gen;
    private TextSearchHandle? _nativeSearch;
    private CancellationTokenSource? _searchCts;
    private readonly ConcurrentQueue<LineHit> _pending = new();
    private List<LineHit> _hits = new();          // 끝에 덧붙이기만 한다 (HitListView 가 보이는 행만 그린다)
    private long _nextFirstMatch;
    private long _totalMatches, _totalLines;   // 작업 스레드가 더한다 (Interlocked)
    private long _searchDoneBytes;
    private volatile bool _searchDone = true;
    private string? _findError;
    private string _find = "";
    private bool _matchCase;
    private Regex? _regex;
    private int _curHit = -1, _curMatch = -1;
    private bool _userMoved;

    /// <param name="find">처음부터 찾을 말 (파일 찾기의 '포함 텍스트')</param>
    public ViewerWindow(string path, string? find = null, bool caseSensitive = false, bool regex = false)
    {
        InitializeComponent();
        _path = path;
        _isImage = ImageExtensions.Contains(Path.GetExtension(path));
        Title = $"보기 - {Path.GetFileName(path)}";

        _syncing = true;
        FindBox.Text = find ?? "";
        CaseCheck.IsChecked = caseSensitive;
        RegexCheck.IsChecked = regex;
        _syncing = false;

        HitList.Items = _hits;
        HitList.VBar = HitBar;
        HitList.RowChosen += i => _ = SelectAsync(i, 0, userAction: true);
        TextView.VBar = VBar;
        TextView.HBar = HBar;
        TextView.Highlighter = HighlightLine;
        TextView.LineActivated += line => _ = ShowDetailAsync(line, -1, 0);
        TextView.UserInteracted += () => _userMoved = true;   // 찾는 중이어도 사용자가 움직이면 자동 이동하지 않는다
        // MPC_TRACE 가 있으면 열기 시간 기록 (성능 점검용)
        var openWatch = System.Diagnostics.Stopwatch.StartNew();
        TextView.FirstPaint += () => Trace($"first paint {openWatch.ElapsedMilliseconds} ms ({Path.GetFileName(path)})");

        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _poll.Tick += (_, _) => Poll();
        _findTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _findTimer.Tick += (_, _) => { _findTimer.Stop(); StartSearch(); };

        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => Shutdown();
        PreviewKeyDown += OnKey;
    }

    internal static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("MPC_TRACE") is not { Length: > 0 } file) return;
        try { File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n"); } catch { }
    }

    private void Shutdown()
    {
        _poll.Stop();
        _findTimer.Stop();
        StopSearch();
        _source?.Dispose();
        _source = null;
    }

    // ───────────────────────── 열기 ─────────────────────────

    private async Task LoadAsync()
    {
        try
        {
            if (_isImage)
            {
                var (data, length) = await GuardedIo.RunAsync(ctx => ReadHead(_path, ImageLimit, ctx), 5000, _path);
                if (TryShowImage(data, length)) return;
            }
            var (head, _) = await GuardedIo.RunAsync(ctx => ReadHead(_path, 64 * 1024, ctx), 5000, _path);
            if (LooksBinary(head)) { await ShowHexAsync(); return; }

            // 줄 색인은 C++ 백그라운드 스레드가 만들고, 첫 줄들은 바로 보인다
            _source = await GuardedIo.RunAsync(_ => TextFileSource.Open(_path), 5000, _path);
            _encodingName = _source.EncodingName;
            TextView.Source = _source;
            TextArea.Visibility = Visibility.Visible;
            StateText.Visibility = Visibility.Collapsed;
            TextView.Focus();
            _poll.Start();
            Poll();
            if (FindBox.Text.Length > 0) StartSearch();
        }
        catch (Exception ex)
        {
            StateText.Text = ex.Message;
        }
    }

    private static (byte[] Data, long Length) ReadHead(string path, int limit, IoContext ctx)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        long length = fs.Length;
        var buf = new byte[(int)Math.Min(length, limit)];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, Math.Min(256 * 1024, buf.Length - read));
            if (n == 0) break;
            read += n;
            ctx.Report();
            if (ctx.IsCancelled) break;
        }
        if (read < buf.Length) Array.Resize(ref buf, read);
        return (buf, length);
    }

    private bool TryShowImage(byte[] data, long length)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            ImageView.Source = bmp;
            ImageScroll.Visibility = Visibility.Visible;
            StateText.Visibility = Visibility.Collapsed;
            FindBar.Visibility = Visibility.Collapsed;
            InfoText.Text = $"{_path}   │   {bmp.PixelWidth} × {bmp.PixelHeight}   │   {PathUtil.FormatSize(length)}   │   Esc 닫기";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ShowHexAsync()
    {
        _hex = true;
        var (data, length) = await GuardedIo.RunAsync(ctx => ReadHead(_path, HexLimit, ctx), 5000, _path);
        var text = await Task.Run(() => HexDump(data, data.Length));
        if (data.Length < length) text += $"\n───── 파일이 커서 앞부분 {PathUtil.FormatSize(data.Length)}만 표시합니다 ─────";
        HexBox.Text = text;
        HexBox.Visibility = Visibility.Visible;
        TextArea.Visibility = Visibility.Collapsed;
        StateText.Visibility = Visibility.Collapsed;
        ShowHitsPane(false);
        FindCount.Text = FindBox.Text.Length > 0 ? "16진수 보기에서는 찾지 않음" : "";
        InfoText.Text = $"{_path}   │   {PathUtil.FormatSize(length)}   │   HEX   │   H: 텍스트/HEX   Esc: 닫기";
        HexBox.Focus();
    }

    private void ShowText()
    {
        _hex = false;
        HexBox.Visibility = Visibility.Collapsed;
        if (_source == null) { _ = LoadAsync(); return; }
        TextArea.Visibility = Visibility.Visible;
        ShowHitsPane(_find.Length > 0);
        UpdateFindCount();
        TextView.Focus();
    }

    /// <summary>0.25초마다: 색인 진행(줄 수), 찾은 줄 붙이기, 개수 표시</summary>
    private void Poll()
    {
        if (_source == null) return;
        TextView.Refresh();
        var st = _source.Status;
        var indexing = st.Done == 0 ? $"   │   색인 중 {(st.FileSize > 0 ? st.IndexedBytes * 100 / st.FileSize : 0)}%" : "";
        InfoText.Text = $"{_path}   │   {PathUtil.FormatSize(st.FileSize)}   │   {_encodingName}   │   {st.LineCount:N0}줄{indexing}"
                      + "   │   Ctrl+F 찾기  F3 다음  Shift+F3 이전   Ctrl+C 복사   H: HEX   W: 줄 바꿈   Esc: 닫기";
        FlushHits();
    }

    // ───────────────────────── 찾기 ─────────────────────────

    private void StopSearch()
    {
        _gen++;
        _nativeSearch?.Dispose();
        _nativeSearch = null;
        _searchCts?.Cancel();
        _searchCts = null;
    }

    /// <summary>찾을 말이 바뀌면 파일 전체 찾기를 새로 시작</summary>
    private void StartSearch()
    {
        StopSearch();
        int gen = _gen;
        while (_pending.TryDequeue(out _)) { }
        _hits = new List<LineHit>();
        HitList.Items = _hits;
        _nextFirstMatch = 0;
        _totalMatches = _totalLines = 0;
        _searchDoneBytes = 0;
        _curHit = _curMatch = -1;
        _userMoved = false;
        _findError = null;
        _find = FindBox.Text;
        _matchCase = CaseCheck.IsChecked == true;
        _regex = null;
        TextView.ClearCurrent();
        LineDetail.Document.Blocks.Clear();

        if (_source == null || _hex || _find.Length == 0)
        {
            _searchDone = true;
            ShowHitsPane(false);
            UpdateFindCount();
            TextView.InvalidateVisual();
            return;
        }
        if (RegexCheck.IsChecked == true)
        {
            try { _regex = TextRegex.Create(_find, _matchCase, compiled: true); }
            catch (ArgumentException ex)
            {
                _findError = "정규식 오류: " + ex.Message;
                _searchDone = true;
                ShowHitsPane(false);
                UpdateFindCount();
                return;
            }
        }
        _searchDone = false;
        ShowHitsPane(true);
        TextView.InvalidateVisual();
        var source = _source;
        string find = _find;
        bool matchCase = _matchCase;
        var re = _regex;

        if (re == null) StartNativeSearch(gen, source, find, matchCase);
        else StartRegexSearch(gen, source, find, matchCase, re);
        UpdateFindCount();
    }

    /// <summary>일반 문자열: C++ 가 파일을 훑어 (줄, 개수) 를 주면, 한 작업 스레드가 그 줄만 읽어 확인·미리보기를 만든다</summary>
    private void StartNativeSearch(int gen, TextFileSource source, string find, bool matchCase)
    {
        var batches = new BlockingCollection<(long[] Lines, int[] Counts, long Done)>(64);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _nativeSearch = source.StartSearch(find, matchCase, (lines, counts, done) =>
        {
            if (gen != _gen || cts.IsCancellationRequested) return false;
            try { batches.Add((lines, counts, done), cts.Token); return true; }
            catch { return false; }
        });
        var handle = _nativeSearch;

        StartBackground("ViewerVerify", () =>
        {
            long listed = 0;
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!batches.TryTake(out var batch, 100, cts.Token))
                    {
                        // 더 올 것이 없고 C++ 도 끝났으면 마무리
                        if (handle.Progress.Done && batches.Count == 0)
                        {
                            if (batches.TryTake(out batch, 300, cts.Token)) goto process;
                            break;
                        }
                        Interlocked.Exchange(ref _searchDoneBytes, handle.Progress.DoneBytes);
                        continue;
                    }
                    process:
                    Interlocked.Exchange(ref _searchDoneBytes, batch.Done);
                    for (int i = 0; i < batch.Lines.Length && !cts.IsCancellationRequested; i++)
                    {
                        long line = batch.Lines[i];
                        if (listed >= MaxListLines)
                        {
                            // 목록은 그만 — 개수만 C++ 값으로 센다
                            Interlocked.Add(ref _totalMatches, batch.Counts[i]);
                            Interlocked.Increment(ref _totalLines);
                            continue;
                        }
                        var text = source.GetLineSync(line);
                        var hit = MakeHit(line, text, find, matchCase, null);
                        if (hit == null) continue;   // 대소문자 후보가 실제로는 다른 글자였음
                        listed++;
                        Interlocked.Add(ref _totalMatches, hit.Count);
                        Interlocked.Increment(ref _totalLines);
                        _pending.Enqueue(hit);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (gen == _gen) _findError = "찾기 실패: " + ex.Message; }
            if (gen == _gen && !cts.IsCancellationRequested) _searchDone = true;
        });
    }

    /// <summary>찾기 작업은 UI 보다 낮은 우선순위의 전용 스레드에서 (찾는 중에도 화면·스크롤이 부드럽게)</summary>
    private static void StartBackground(string name, Action work)
        => new Thread(() => work()) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal }.Start();

    /// <summary>정규식: C# 여러 스레드로 파일 전체 (LargeTextSearch)</summary>
    private void StartRegexSearch(int gen, TextFileSource source, string find, bool matchCase, Regex re)
    {
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        StartBackground("ViewerRegex", () =>
        {
            try
            {
                LargeTextSearch.Search(source.Path, source.Encoding, source.BomLength, find, matchCase, re, MaxListLines, PreviewChars, p =>
                {
                    if (gen != _gen) return;
                    foreach (var h in p.NewHits)
                    {
                        var hit = new LineHit { Line = h.LineNumber - 1, Count = h.Count, Preview = h.Preview };
                        hit.Offsets.AddRange(h.Offsets);
                        _pending.Enqueue(hit);
                    }
                    Interlocked.Exchange(ref _totalMatches, p.Matches);
                    Interlocked.Exchange(ref _totalLines, p.MatchLines);
                    Interlocked.Exchange(ref _searchDoneBytes, p.DoneBytes);
                }, cts.Token);
                if (gen == _gen) _searchDone = true;
            }
            catch (OperationCanceledException) { }
            catch (RegexMatchTimeoutException) { if (gen == _gen) { _findError = "정규식이 너무 오래 걸려 멈췄습니다"; _searchDone = true; } }
            catch (Exception ex) { if (gen == _gen) { _findError = "찾기 실패: " + ex.Message; _searchDone = true; } }
        });
    }

    private static LineHit? MakeHit(long line, string text, string find, bool matchCase, Regex? re)
    {
        var matches = TextMatchCount.Matches(text, find, matchCase, re).ToList();
        if (matches.Count == 0) return null;
        var preview = text.Length > PreviewChars ? text.Substring(0, PreviewChars) + " …" : text;
        preview = preview.Replace('\t', ' ');
        var hit = new LineHit { Line = line, Count = matches.Count, Preview = preview };
        foreach (var (s, l) in matches) if (s < PreviewChars) hit.Offsets.Add((s, l));
        return hit;
    }

    /// <summary>찾은 줄을 목록 끝에 붙인다 (지우거나 다시 만들지 않으므로 선택·스크롤이 그대로)</summary>
    private void FlushHits()
    {
        int added = 0;
        while (_pending.TryDequeue(out var hit))
        {
            hit.FirstMatch = _nextFirstMatch;
            _nextFirstMatch += hit.Count;
            _hits.Add(hit);
            added++;
        }
        if (added > 0) HitList.Refresh();
        // 처음 찾은 것으로 바로 이동 (사용자가 아직 움직이지 않았을 때만)
        if (!_userMoved && _curHit < 0 && _hits.Count > 0) _ = SelectAsync(0, 0, userAction: false);
        UpdateFindCount();
    }

    private void UpdateFindCount()
    {
        if (_find.Length == 0 && FindBox.Text.Length == 0) { FindCount.Text = ""; return; }
        if (_hex) { FindCount.Text = "16진수 보기에서는 찾지 않음"; return; }
        if (_findError != null) { FindCount.Text = _findError; FindCount.ToolTip = _findError; return; }
        FindCount.ToolTip = null;
        long total = Interlocked.Read(ref _totalMatches), lines = Interlocked.Read(ref _totalLines);
        long size = _source?.Status.FileSize ?? 0;
        string progress = _searchDone ? "" : $" · 찾는 중 {(size > 0 ? Math.Min(99, Interlocked.Read(ref _searchDoneBytes) * 100 / size) : 0)}%";
        if (_searchDone && total == 0) { FindCount.Text = "없음"; return; }
        string pos = _curHit >= 0 && _curHit < _hits.Count ? $"{_hits[_curHit].FirstMatch + _curMatch + 1:N0} / " : "";
        string cut = _totalLines > MaxListLines ? $" (목록 {MaxListLines:N0}줄)" : "";
        FindCount.Text = $"{pos}{total:N0}개 · {lines:N0}줄{cut}{progress}";
    }

    private IReadOnlyList<(int Start, int Len)> HighlightLine(string text)
    {
        if (_find.Length == 0 || _findError != null) return Array.Empty<(int, int)>();
        try { return TextMatchCount.Matches(text, _find, _matchCase, _regex).Take(200).ToList(); }
        catch { return Array.Empty<(int, int)>(); }
    }

    // ───────────────────────── 이동 ─────────────────────────

    /// <summary>hitIndex 번째 찾은 줄의 matchIndex 번째 일치로 (matchIndex = -1 이면 마지막)</summary>
    private async Task SelectAsync(int hitIndex, int matchIndex, bool userAction)
    {
        if (_source == null || hitIndex < 0 || hitIndex >= _hits.Count) return;
        if (userAction) _userMoved = true;
        var hit = _hits[hitIndex];
        var source = _source;
        string text = source.TryGetLine(hit.Line) ?? await Task.Run(() => source.GetLineSync(hit.Line));
        if (source != _source) return;
        var matches = HighlightLine(text);
        if (matches.Count == 0) return;
        int mi = matchIndex < 0 ? matches.Count - 1 : Math.Min(matchIndex, matches.Count - 1);
        _curHit = hitIndex;
        _curMatch = mi;
        var (start, len) = matches[mi];
        TextView.ShowMatch(hit.Line, start, len);

        HitList.SelectedIndex = hitIndex;
        ShowDetail(text, matches, mi);
        UpdateFindCount();
    }

    private void Next(int dir)
    {
        if (_hits.Count == 0)
        {
            UpdateFindCount();
            return;
        }
        if (_curHit < 0)
        {
            // 지금 화면 위치에서 가까운 찾은 줄부터
            long top = TextView.TopLine;
            int i = LowerBound(top);
            if (dir < 0) i--;
            if (i < 0) i = _hits.Count - 1;
            if (i >= _hits.Count) i = _searchDone ? 0 : _hits.Count - 1;
            _ = SelectAsync(i, dir > 0 ? 0 : -1, userAction: true);
            return;
        }
        var cur = _hits[_curHit];
        if (dir > 0 && _curMatch + 1 < cur.Count) { _ = SelectAsync(_curHit, _curMatch + 1, true); return; }
        if (dir < 0 && _curMatch > 0) { _ = SelectAsync(_curHit, _curMatch - 1, true); return; }
        int next = _curHit + dir;
        if (next >= _hits.Count)
        {
            if (!_searchDone) { FindCount.Text = "다음 일치를 찾는 중..."; return; }
            next = 0;
        }
        if (next < 0) next = _hits.Count - 1;
        _ = SelectAsync(next, dir > 0 ? 0 : -1, userAction: true);
    }

    /// <summary>첫 번째로 Line >= line 인 찾은 줄</summary>
    private int LowerBound(long line)
    {
        int lo = 0, hi = _hits.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (_hits[mid].Line < line) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // ───────────────────────── 아래 칸 ─────────────────────────

    private void ShowHitsPane(bool show)
    {
        if (show && HitsRow.Height.Value == 0)
        {
            SplitRow.Height = new GridLength(5);
            HitsRow.Height = new GridLength(Math.Max(200, MainGrid.ActualHeight * 0.4));
        }
        else if (!show)
        {
            SplitRow.Height = new GridLength(0);
            HitsRow.Height = new GridLength(0);
        }
    }

    /// <summary>고른 줄 전체를 아래 칸에 (자동 줄 바꿈), 찾은 말은 노란색 / 지금 것은 주황</summary>
    private void ShowDetail(string text, IReadOnlyList<(int Start, int Len)> matches, int current)
    {
        var para = new Paragraph { Margin = new Thickness(0) };
        int at = 0;
        for (int i = 0; i < matches.Count; i++)
        {
            var (m, len) = matches[i];
            if (m < at || m >= text.Length) continue;
            if (m > at) para.Inlines.Add(new Run(text.Substring(at, m - at)));
            int l = Math.Min(len, text.Length - m);
            para.Inlines.Add(new Run(text.Substring(m, l))
            {
                Background = i == current ? Brushes.Orange : Brushes.Yellow,
                FontWeight = i == current ? FontWeights.Bold : FontWeights.Normal,
            });
            at = m + l;
        }
        if (at < text.Length) para.Inlines.Add(new Run(text.Substring(at)));
        LineDetail.Document.Blocks.Clear();
        LineDetail.Document.Blocks.Add(para);
        LineDetail.ScrollToHome();
    }

    /// <summary>본문에서 더블클릭한 줄을 아래 칸에 (찾기 목록이 없어도 보이게)</summary>
    private async Task ShowDetailAsync(long line, int hitIndex, int match)
    {
        if (_source == null) return;
        var source = _source;
        string text = source.TryGetLine(line) ?? await Task.Run(() => source.GetLineSync(line));
        if (HitsRow.Height.Value == 0) ShowHitsPane(true);
        ShowDetail(text, HighlightLine(text), -1);
    }

    // ───────────────────────── 이벤트 ─────────────────────────

    private void HitBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if ((int)e.NewValue != HitList.TopRow) HitList.ScrollTo((int)e.NewValue);
    }

    private void FindBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;
        _findTimer.Stop();
        _findTimer.Start();   // 입력이 멈추면 찾기 시작
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || _source == null) return;
        StartSearch();
    }

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FlushFind();
            Next(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : +1);
            e.Handled = true;
        }
    }

    /// <summary>입력 대기 중인 찾기를 바로 시작</summary>
    private void FlushFind()
    {
        if (_findTimer.IsEnabled || FindBox.Text != _find)
        {
            _findTimer.Stop();
            StartSearch();
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e) { FlushFind(); Next(+1); }
    private void Prev_Click(object sender, RoutedEventArgs e) { FlushFind(); Next(-1); }

    private void VBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if ((long)e.NewValue == TextView.TopLine) return;   // 프로그램이 맞춘 값
        _userMoved = true;
        TextView.ScrollTo((long)e.NewValue);
    }
    private void HBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => TextView.ScrollHorizontal(e.NewValue);

    /// <summary>Ctrl+C: 선택한 글자 범위 복사 (여러 줄이면 백그라운드에서 읽어 붙인다)</summary>
    private async void CopySelection()
    {
        if (_source == null || TextView.SelectionRange is not { } sel) return;
        var source = _source;
        var (from, to) = sel;
        var text = await Task.Run(() =>
        {
            var sb = new StringBuilder();
            for (long line = from.Line; line <= to.Line && sb.Length < MaxCopyChars; line++)
            {
                var t = source.GetLineSync(line);
                int a = line == from.Line ? Math.Min(from.Col, t.Length) : 0;
                // 그린 범위(4000자)를 넘는 열은 줄 끝까지로 본다
                int b = line == to.Line && to.Col < VirtualTextView.MaxDrawChars ? Math.Min(to.Col, t.Length) : t.Length;
                if (line > from.Line) sb.Append("\r\n");
                if (b > a) sb.Append(t, a, b - a);
            }
            return sb.Length > MaxCopyChars ? sb.ToString(0, MaxCopyChars) : sb.ToString();
        });
        try
        {
            Clipboard.SetText(text);
            long lines = to.Line - from.Line + 1;
            FindCount.Text = lines > 1 ? $"{lines:N0}줄 ({text.Length:N0}자) 복사함" : $"{text.Length:N0}자 복사함";
        }
        catch { }
    }

    private static bool LooksBinary(byte[] d)
    {
        if (d.Length >= 2 && ((d[0] == 0xFF && d[1] == 0xFE) || (d[0] == 0xFE && d[1] == 0xFF))) return false;
        int n = Math.Min(d.Length, 8192), zeros = 0;
        for (int i = 0; i < n; i++) if (d[i] == 0) zeros++;
        return zeros > 0 && zeros * 100 / Math.Max(1, n) >= 1;
    }

    private static string HexDump(byte[] d, int length)
    {
        var sb = new StringBuilder(length * 4 + length / 16 * 14);
        for (int off = 0; off < length; off += 16)
        {
            sb.Append(off.ToString("X8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (off + i < length) sb.Append(d[off + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(" │ ");
            for (int i = 0; i < 16 && off + i < length; i++)
            {
                byte b = d[off + i];
                sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool typing = Keyboard.FocusedElement == FindBox;
        bool textMode = TextArea.Visibility == Visibility.Visible;
        switch (key)
        {
            // F3: 찾을 말이 있으면 다음(Shift+F3 이전), 없으면 닫기 (Double Commander 처럼)
            case Key.F3 when FindBox.Text.Length > 0 && textMode:
                FlushFind();
                Next(mods == ModifierKeys.Shift ? -1 : +1);
                e.Handled = true;
                break;
            case Key.F when mods == ModifierKeys.Control && FindBar.Visibility == Visibility.Visible:
                FindBox.Focus();
                FindBox.SelectAll();
                e.Handled = true;
                break;
            case Key.C when mods == ModifierKeys.Control && textMode && !typing && Keyboard.FocusedElement is not System.Windows.Controls.RichTextBox:
                CopySelection();
                e.Handled = true;
                break;
            case Key.A when mods == ModifierKeys.Control && textMode && !typing && Keyboard.FocusedElement is not System.Windows.Controls.RichTextBox:
                TextView.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape or Key.F3 or Key.F10:
                Close();
                e.Handled = true;
                break;
            case Key.H or Key.W when typing:
                break;   // 찾기 칸에 입력 중인 글자
            case Key.H when !_isImage || _source != null:
                if (_hex) ShowText(); else _ = ShowHexAsync();
                e.Handled = true;
                break;
            case Key.W when textMode:
                TextView.Wrap = !TextView.Wrap;
                e.Handled = true;
                break;
        }
    }
}
