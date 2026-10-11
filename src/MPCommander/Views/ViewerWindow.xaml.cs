using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// F3 내장 뷰어. 텍스트(인코딩 자동 판별) / 16진수 / 이미지. 큰 파일은 앞부분만 읽는다.
/// 찾기: 위쪽 찾기 막대(Ctrl+F), F3 다음 / Shift+F3 이전. 찾은 줄은 아래 목록에 모두 나오고,
/// 고른 줄의 전체 내용은 그 아래에 자동 줄 바꿈으로 보인다. 파일 찾기(Alt+F7)에서 열면 '포함 텍스트'로 바로 찾는다.
/// </summary>
public partial class ViewerWindow : Window
{
    private const int TextLimit = 8 * 1024 * 1024;
    private const int HexLimit = 512 * 1024;
    private const int ImageLimit = 64 * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };

    private readonly string _path;
    private byte[] _data = [];
    private long _fileLength;
    private bool _hex, _isImage;
    private string _encodingName = "";

    private const int MaxMatches = 200_000;
    private const int MaxHitLines = 20_000;
    private const int PreviewChars = 400;

    /// <summary>찾은 줄 하나 (아래 목록의 한 행)</summary>
    private sealed class LineHit
    {
        public int LineNumber { get; init; }
        public int Start { get; init; }
        public int FirstMatch { get; init; }
        public int Count { get; set; }
        public string Preview { get; init; } = "";
        /// <summary>미리보기 안에서 찾은 말의 (시작, 길이)들</summary>
        public List<(int Off, int Len)> Offsets { get; } = new();
        public string CountText => Count > 1 ? Count.ToString() : "";

        /// <summary>목록에 칠해 보일 조각: (글자, 찾은 말인지)</summary>
        public IReadOnlyList<(string Text, bool Hit)> Segments
        {
            get
            {
                var list = new List<(string, bool)>();
                int at = 0;
                foreach (var (o, mlen) in Offsets)
                {
                    if (o < at || o >= Preview.Length) continue;
                    if (o > at) list.Add((Preview.Substring(at, o - at), false));
                    int len = Math.Min(mlen, Preview.Length - o);
                    list.Add((Preview.Substring(o, len), true));
                    at = o + len;
                }
                if (at < Preview.Length) list.Add((Preview.Substring(at), false));
                return list;
            }
        }
    }

    private string _text = "";
    private List<int> _lineStarts = new();
    private readonly List<int> _matches = new();
    private readonly List<int> _lens = new();   // 일치마다 길이 (정규식이면 서로 다름)
    private string? _findError;
    private readonly List<LineHit> _hits = new();
    private int _cur = -1;
    private bool _syncing;
    private readonly DispatcherTimer _findTimer;

    /// <param name="find">처음부터 찾을 말 (파일 찾기의 '포함 텍스트')</param>
    public ViewerWindow(string path, string? find = null, bool caseSensitive = false, bool regex = false)
    {
        InitializeComponent();
        _findTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _findTimer.Tick += (_, _) => { _findTimer.Stop(); RebuildMatches(); GoTo(0); };
        _syncing = true;
        FindBox.Text = find ?? "";
        CaseCheck.IsChecked = caseSensitive;
        RegexCheck.IsChecked = regex;
        _syncing = false;
        _path = path;
        Title = $"보기 - {Path.GetFileName(path)}";
        _isImage = ImageExtensions.Contains(Path.GetExtension(path));
        Loaded += async (_, _) => await LoadAsync();
        TextView.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent, new System.Windows.Controls.ScrollChangedEventHandler((_, _) => UpdateMark()));
        TextView.SizeChanged += (_, _) => UpdateMark();
        PreviewKeyDown += OnKey;
    }

    private async Task LoadAsync()
    {
        try
        {
            int limit = _isImage ? ImageLimit : TextLimit;
            (_data, _fileLength) = await GuardedIo.RunAsync(ctx => ReadHead(_path, limit, ctx), 5000, _path);
        }
        catch (Exception ex)
        {
            StateText.Text = ex.Message;
            return;
        }

        if (_isImage && TryShowImage()) return;
        _hex = LooksBinary(_data);
        ShowText();
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

    private bool TryShowImage()
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(_data);
            bmp.EndInit();
            bmp.Freeze();
            ImageView.Source = bmp;
            ImageScroll.Visibility = Visibility.Visible;
            StateText.Visibility = Visibility.Collapsed;
            FindBar.Visibility = Visibility.Collapsed;
            InfoText.Text = $"{_path}   │   {bmp.PixelWidth} × {bmp.PixelHeight}   │   {PathUtil.FormatSize(_fileLength)}   │   Esc 닫기";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ShowText()
    {
        string text;
        if (_hex)
        {
            text = HexDump(_data, Math.Min(_data.Length, HexLimit));
            _encodingName = "HEX";
        }
        else
        {
            var enc = TextMatchCount.DetectEncoding(_data, out int bomLength);
            _encodingName = enc.WebName.ToUpperInvariant();
            text = enc.GetString(_data, bomLength, _data.Length - bomLength);
        }

        long shown = _hex ? Math.Min(_data.Length, HexLimit) : _data.Length;
        if (shown < _fileLength) text += $"\n\n───── 파일이 커서 앞부분 {PathUtil.FormatSize(shown)}만 표시합니다 ─────";

        TextView.Text = text;
        _text = text;
        _lineStarts = LineStarts(text);
        TextView.Visibility = Visibility.Visible;
        StateText.Visibility = Visibility.Collapsed;
        TextView.Focus();
        TextView.CaretIndex = 0;
        TextView.ScrollToHome();
        InfoText.Text = $"{_path}   │   {PathUtil.FormatSize(_fileLength)}   │   {_encodingName}   │   Ctrl+F 찾기  F3 다음  Shift+F3 이전   H: 텍스트/HEX   W: 줄 바꿈   Esc: 닫기";

        RebuildMatches();
        GoTo(0);
    }

    // ───────────────────────── 찾기 ─────────────────────────

    private string FindText => FindBox.Text;

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1)) starts.Add(i + 1);
        return starts;
    }

    /// <summary>글자 위치가 몇 번째 줄(0부터)인지</summary>
    private int LineOf(int pos)
    {
        int i = _lineStarts.BinarySearch(pos);
        return i >= 0 ? i : ~i - 1;
    }

    private (int Start, int Length) LineSpan(int line)
    {
        int start = _lineStarts[line];
        int end = line + 1 < _lineStarts.Count ? _lineStarts[line + 1] : _text.Length;
        int len = end - start;
        while (len > 0 && (_text[start + len - 1] == '\n' || _text[start + len - 1] == '\r')) len--;
        return (start, len);
    }

    /// <summary>찾을 말이 바뀌면 모든 위치와 찾은 줄 목록을 다시 만든다 (16진수 보기에서는 하지 않음)</summary>
    private void RebuildMatches()
    {
        _matches.Clear();
        _lens.Clear();
        _hits.Clear();
        _cur = -1;
        _findError = null;
        var find = FindText;
        bool active = !_hex && find.Length > 0 && _text.Length > 0;
        if (active)
        {
            LineHit? last = null;
            try
            {
                foreach (var (i, mlen) in EnumerateMatches(find))
                {
                    if (_matches.Count >= MaxMatches) break;
                    _matches.Add(i);
                    _lens.Add(mlen);
                    int line = LineOf(i);
                    if (last != null && last.LineNumber == line + 1) { last.Count++; last.Offsets.Add((i - last.Start, mlen)); continue; }
                    if (_hits.Count >= MaxHitLines) { last = null; continue; }
                    var (start, len) = LineSpan(line);
                    var preview = _text.Substring(start, Math.Min(len, PreviewChars)).Replace('\t', ' ');
                    if (len > PreviewChars) preview += " …";
                    last = new LineHit { LineNumber = line + 1, Start = start, FirstMatch = _matches.Count - 1, Count = 1, Preview = preview };
                    last.Offsets.Add((i - start, mlen));
                    _hits.Add(last);
                }
            }
            catch (ArgumentException ex) { _findError = "정규식 오류: " + ex.Message; }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { _findError = "정규식이 너무 오래 걸려 멈췄습니다"; }
        }

        _syncing = true;
        HitList.ItemsSource = null;
        HitList.ItemsSource = _hits;
        LineDetail.Document.Blocks.Clear();
        _syncing = false;
        ShowHitsPane(active);
        UpdateFindCount();
        UpdateMark();
    }

    /// <summary>찾을 말의 모든 (위치, 길이). 정규식이면 Regex (줄 단위 ^ $), 아니면 단순 문자열 — 파일 찾기 결과의 개수 세기와 같은 규칙</summary>
    private IEnumerable<(int Pos, int Len)> EnumerateMatches(string find)
    {
        bool matchCase = CaseCheck.IsChecked == true;
        var re = RegexCheck.IsChecked == true ? TextRegex.Create(find, matchCase) : null;
        return TextMatchCount.Matches(_text, find, matchCase, re);
    }

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

    private void UpdateFindCount()
    {
        if (FindText.Length == 0) { FindCount.Text = ""; return; }
        if (_hex) { FindCount.Text = "16진수 보기에서는 찾지 않음"; return; }
        if (_findError != null) { FindCount.Text = _findError; return; }
        if (_matches.Count == 0) { FindCount.Text = "없음"; return; }
        var more = _matches.Count >= MaxMatches ? "+" : "";
        var lines = _hits.Count >= MaxHitLines ? $"앞 {MaxHitLines:N0}줄만 목록" : $"{_hits.Count:N0}줄";
        FindCount.Text = _cur >= 0 ? $"{_cur + 1:N0} / {_matches.Count:N0}{more}개 · {lines}" : $"{_matches.Count:N0}{more}개 · {lines}";
    }

    /// <summary>from 위치 이후 첫 일치로 이동</summary>
    private void GoTo(int from)
    {
        if (_matches.Count == 0) return;
        int i = _matches.BinarySearch(from);
        if (i < 0) i = ~i;
        Select(i >= _matches.Count ? 0 : i);
    }

    private void Next(int dir)
    {
        if (_matches.Count == 0) return;
        if (_cur < 0)
        {
            // 커서 위치에서 이어서 찾기
            int i = _matches.BinarySearch(TextView.CaretIndex);
            if (i < 0) i = ~i;
            Select(dir > 0 ? (i >= _matches.Count ? 0 : i) : (i - 1 < 0 ? _matches.Count - 1 : i - 1));
            return;
        }
        Select((_cur + dir + _matches.Count) % _matches.Count);
    }

    /// <summary>i 번째 일치를 본문에서 선택·표시하고, 아래 목록과 줄 전체 보기를 맞춘다</summary>
    private void Select(int i)
    {
        _cur = i;
        int pos = _matches[i], len = _lens[i];
        TextView.Select(pos, len);
        try
        {
            int line = TextView.GetLineIndexFromCharacterIndex(pos);
            if (line >= 0) TextView.ScrollToLine(Math.Max(0, line - 3));
            var rect = TextView.GetRectFromCharacterIndex(pos);
            if (!rect.IsEmpty && (rect.X < 0 || rect.X > TextView.ViewportWidth - 40))
                TextView.ScrollToHorizontalOffset(Math.Max(0, TextView.HorizontalOffset + rect.X - 40));
        }
        catch { }

        int lineIndex = LineOf(pos);
        var hit = FindHit(lineIndex + 1);
        _syncing = true;
        HitList.SelectedItem = hit;
        if (hit != null) HitList.ScrollIntoView(hit);
        _syncing = false;
        ShowLineDetail(lineIndex, pos, len);
        UpdateFindCount();
        Dispatcher.BeginInvoke(new Action(UpdateMark), DispatcherPriority.Loaded);   // 스크롤이 끝난 뒤 위치 계산
    }

    /// <summary>본문에서 지금 찾은 말 위에 주황 상자를 둔다 (보이는 곳에 있을 때만)</summary>
    private void UpdateMark()
    {
        if (_cur < 0 || _cur >= _matches.Count || TextView.Visibility != Visibility.Visible || _hex)
        {
            MarkRect.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            int pos = _matches[_cur], len = _lens[_cur];
            var a = TextView.GetRectFromCharacterIndex(pos);
            var b = TextView.GetRectFromCharacterIndex(Math.Min(_text.Length, pos + len));
            if (a.IsEmpty || b.IsEmpty) { MarkRect.Visibility = Visibility.Collapsed; return; }
            double width = Math.Abs(b.Y - a.Y) < 1 ? b.X - a.X : TextView.ViewportWidth - a.X;   // 줄 바꿈으로 나뉘면 줄 끝까지
            var topLeft = TextView.TranslatePoint(new Point(a.X, a.Y), MarkLayer);
            bool visible = topLeft.Y + a.Height > 0 && topLeft.Y < MarkLayer.ActualHeight && width > 0;
            MarkRect.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) return;
            System.Windows.Controls.Canvas.SetLeft(MarkRect, topLeft.X - 1);
            System.Windows.Controls.Canvas.SetTop(MarkRect, topLeft.Y);
            MarkRect.Width = width + 2;
            MarkRect.Height = a.Height;
        }
        catch
        {
            MarkRect.Visibility = Visibility.Collapsed;
        }
    }

    private LineHit? FindHit(int lineNumber)
    {
        int lo = 0, hi = _hits.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int n = _hits[mid].LineNumber;
            if (n == lineNumber) return _hits[mid];
            if (n < lineNumber) lo = mid + 1; else hi = mid - 1;
        }
        return null;
    }

    /// <summary>고른 줄 전체를 아래 칸에 (자동 줄 바꿈), 찾은 말은 모두 노란색으로</summary>
    private void ShowLineDetail(int line, int pos, int len)
    {
        var (start, length) = LineSpan(line);
        var para = new Paragraph { Margin = new Thickness(0) };
        int at = start, end = start + length;
        int i = _matches.BinarySearch(start);
        if (i < 0) i = ~i;
        for (; i < _matches.Count && _matches[i] < end; i++)
        {
            int m = _matches[i];
            if (m > at) para.Inlines.Add(new Run(_text.Substring(at, m - at)));
            int mlen = Math.Min(_lens[i], end - m);
            para.Inlines.Add(new Run(_text.Substring(m, mlen))
            {
                Background = m == pos ? Brushes.Orange : Brushes.Yellow,
                FontWeight = m == pos ? FontWeights.Bold : FontWeights.Normal,
            });
            at = m + mlen;
        }
        if (at < end) para.Inlines.Add(new Run(_text.Substring(at, end - at)));
        LineDetail.Document.Blocks.Clear();
        LineDetail.Document.Blocks.Add(para);
        LineDetail.ScrollToHome();
    }

    private void HitList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncing || HitList.SelectedItem is not LineHit hit) return;
        Select(hit.FirstMatch);
    }

    private void FindBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;
        _findTimer.Stop();
        _findTimer.Start();   // 큰 파일에서 글자마다 다시 찾지 않도록 잠깐 기다렸다가
    }

    private void Case_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing || TextView == null || _text.Length == 0) return;
        RebuildMatches();
        GoTo(0);
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

    /// <summary>입력 대기 중인 찾기를 바로 적용</summary>
    private void FlushFind()
    {
        if (!_findTimer.IsEnabled) return;
        _findTimer.Stop();
        RebuildMatches();
    }

    private void Next_Click(object sender, RoutedEventArgs e) { FlushFind(); Next(+1); FocusText(); }
    private void Prev_Click(object sender, RoutedEventArgs e) { FlushFind(); Next(-1); FocusText(); }

    /// <summary>본문에 포커스: 선택(찾은 말)이 진하게 보이고, 이어서 F3 으로 넘길 수 있다</summary>
    private void FocusText()
    {
        if (TextView.Visibility == Visibility.Visible && Keyboard.FocusedElement != FindBox) TextView.Focus();
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
        switch (key)
        {
            // F3: 찾을 말이 있으면 다음(Shift+F3 이전), 없으면 닫기 (Double Commander 처럼)
            case Key.F3 when FindText.Length > 0 && TextView.Visibility == Visibility.Visible:
                FlushFind();
                Next(mods == ModifierKeys.Shift ? -1 : +1);
                e.Handled = true;
                break;
            case Key.F when mods == ModifierKeys.Control && FindBar.Visibility == Visibility.Visible:
                FindBox.Focus();
                FindBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape or Key.F3 or Key.F10:
                Close();
                e.Handled = true;
                break;
            case Key.H or Key.W when typing:
                break;   // 찾기 칸에 입력 중인 글자
            case Key.H when !_isImage || TextView.Visibility == Visibility.Visible:
                if (_data.Length == 0) break;
                _hex = !_hex;
                ShowText();
                e.Handled = true;
                break;
            case Key.W when TextView.Visibility == Visibility.Visible:
                TextView.TextWrapping = TextView.TextWrapping == TextWrapping.Wrap ? TextWrapping.NoWrap : TextWrapping.Wrap;
                e.Handled = true;
                break;
        }
    }
}
