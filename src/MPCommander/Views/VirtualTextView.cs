using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MPCommander.IO;

namespace MPCommander.Views;

/// <summary>
/// 보이는 줄만 그리는 텍스트 보기 (수 GB 파일도 바로 열림). 줄은 TextFileSource 가 블록 단위로 읽어 준다.
///  - 왼쪽에 줄 번호, 세로 스크롤은 파일 전체 줄, 가로 스크롤은 지금 보이는 줄의 최대 폭.
///  - 글자 단위 선택: 끌기 / Shift+클릭 / 더블클릭(단어) / Ctrl+A, 복사는 뷰어가 SelectionRange 로.
///  - 찾은 말은 노란색, 지금 것은 주황(테두리).
///  - 그리기는 캐시에 있는 줄만 쓰고 없으면 '…' 로 두었다가 읽히면 다시 그린다 → UI 가 디스크를 기다리지 않는다.
/// </summary>
internal sealed class VirtualTextView : FrameworkElement
{
    public const int MaxDrawChars = 4000;     // 한 줄에서 그리는 최대 글자
    private static readonly Typeface Face = new(new FontFamily("Consolas, D2Coding, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush GutterBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)));
    private static readonly Brush GutterText = Frozen(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)));
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xF0, 0x40)));
    private static readonly Brush CurrentBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xA5, 0x00)));
    private static readonly Pen CurrentPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x70, 0x00)), 1.5));
    private static readonly Brush SelectBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0x66, 0xA8, 0xFF)));
    private static readonly Brush CurrentLineBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE1)));
    private static readonly Brush PendingBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

    /// <summary>글자 위치 (줄, 열)</summary>
    public readonly record struct TextPos(long Line, int Col) : IComparable<TextPos>
    {
        public int CompareTo(TextPos o) => Line != o.Line ? Line.CompareTo(o.Line) : Col.CompareTo(o.Col);
    }

    private sealed record RowLayout(long Line, double Y, double Height, FormattedText Text, int Length, Point Origin);

    private TextFileSource? _source;
    private long _lineCount;
    private long _top;
    private double _hOffset, _maxWidth;
    private double _fontSize = 13, _lineHeight = 17, _charWidth = 7;
    private bool _wrap;
    private TextPos? _anchor, _caret;          // 선택 (anchor → caret)
    private bool _dragging;
    private readonly List<RowLayout> _layout = new();
    private bool _painted;

    public ScrollBar? VBar { get; set; }
    public ScrollBar? HBar { get; set; }
    public Func<string, IReadOnlyList<(int Start, int Len)>>? Highlighter { get; set; }
    public (long Line, int Start, int Len)? Current { get; private set; }

    public event Action<long>? LineActivated;     // 더블클릭한 줄
    public event Action? UserInteracted;          // 사용자가 스크롤·클릭했다 (찾는 중 자동 이동을 멈추게)
    public event Action? FirstPaint;

    public VirtualTextView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        MeasureFont();
    }

    public TextFileSource? Source
    {
        get => _source;
        set
        {
            if (_source != null) _source.BlockLoaded -= OnBlockLoaded;
            _source = value;
            if (_source != null) _source.BlockLoaded += OnBlockLoaded;
            _top = 0;
            _hOffset = 0;
            _anchor = _caret = null;
            Current = null;
            _lineCount = -1;
            Refresh();
        }
    }

    public bool Wrap
    {
        get => _wrap;
        set { _wrap = value; _hOffset = 0; UpdateBars(); InvalidateVisual(); }
    }

    private const double BaseFontSize = 13;

    /// <summary>글자 확대 비율 (Ctrl+휠, 1 = 100%)</summary>
    public double Zoom
    {
        get => _fontSize / BaseFontSize;
        set
        {
            _fontSize = BaseFontSize * value;
            MeasureFont();
            UpdateBars();
            InvalidateVisual();
        }
    }

    public long LineCount => _lineCount;
    public long TopLine => _top;
    public int VisibleLines => Math.Max(1, (int)(ActualHeight / _lineHeight));

    /// <summary>선택 범위 (앞, 뒤). 없으면 null</summary>
    public (TextPos From, TextPos To)? SelectionRange
    {
        get
        {
            if (_anchor is not { } a || _caret is not { } c || a.CompareTo(c) == 0) return null;
            return a.CompareTo(c) < 0 ? (a, c) : (c, a);
        }
    }

    private void MeasureFont()
    {
        var ft = new FormattedText("0123456789", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, _fontSize, Brushes.Black, 1.0);
        _charWidth = ft.WidthIncludingTrailingWhitespace / 10;
        _lineHeight = Math.Ceiling(ft.Height + 2);
    }

    private void OnBlockLoaded() => Dispatcher.BeginInvoke(new Action(InvalidateVisual), System.Windows.Threading.DispatcherPriority.Render);

    /// <summary>색인 진행에 맞춰 줄 수와 스크롤 막대를 갱신 (뷰어가 주기적으로 부른다)</summary>
    public void Refresh()
    {
        long n = _source?.Status.LineCount ?? 0;
        if (n != _lineCount) { _lineCount = n; UpdateBars(); InvalidateVisual(); }
    }

    // 줄 번호 칸: 천 단위 쉼표까지 포함한 가장 긴 번호 + 여백
    private double GutterWidth => Math.Max(4, Math.Max(_lineCount, 1).ToString("N0", CultureInfo.InvariantCulture).Length + 1) * _charWidth + 12;

    private void UpdateBars()
    {
        if (VBar != null)
        {
            VBar.Minimum = 0;
            VBar.Maximum = Math.Max(0, _lineCount - VisibleLines + 1);
            VBar.ViewportSize = VisibleLines;
            VBar.LargeChange = Math.Max(1, VisibleLines - 1);
            VBar.SmallChange = 1;
            VBar.Value = _top;
        }
        if (HBar != null)
        {
            double view = Math.Max(0, ActualWidth - GutterWidth);
            HBar.Visibility = _wrap || _maxWidth <= view ? Visibility.Collapsed : Visibility.Visible;
            HBar.Minimum = 0;
            HBar.Maximum = Math.Max(0, _maxWidth - view + 20);
            HBar.ViewportSize = view;
            HBar.LargeChange = Math.Max(10, view - 40);
            HBar.SmallChange = _charWidth * 4;
            HBar.Value = _hOffset;
        }
    }

    public void ScrollTo(long top)
    {
        long max = Math.Max(0, _lineCount - VisibleLines + 1);
        long t = Math.Max(0, Math.Min(max, top));
        if (t == _top) return;
        _top = t;
        UpdateBars();
        InvalidateVisual();
    }

    public void ScrollHorizontal(double x)
    {
        _hOffset = Math.Max(0, Math.Min(x, Math.Max(0, _maxWidth)));
        UpdateBars();
        InvalidateVisual();
    }

    /// <summary>찾은 말로 이동: 그 줄이 화면 위쪽 1/3 쯤에 오게, 글자가 가로로 보이게. 찾은 말을 선택해 둔다(바로 Ctrl+C 가능)</summary>
    public void ShowMatch(long line, int start, int len)
    {
        Current = (line, start, len);
        _anchor = new TextPos(line, start);
        _caret = new TextPos(line, start + len);
        if (line < _top || line >= _top + VisibleLines - 1) ScrollTo(line - VisibleLines / 3);
        if (!_wrap)
        {
            double x = start * _charWidth, view = Math.Max(0, ActualWidth - GutterWidth);
            if (x < _hOffset || x + len * _charWidth > _hOffset + view - 20) _hOffset = Math.Max(0, x - view / 3);
        }
        UpdateBars();
        InvalidateVisual();
    }

    public void ClearCurrent()
    {
        Current = null;
        InvalidateVisual();
    }

    public void SelectAll()
    {
        if (_lineCount <= 0) return;
        _anchor = new TextPos(0, 0);
        _caret = new TextPos(_lineCount - 1, int.MaxValue);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateBars();
    }

    private static string Clip(string s)
    {
        var t = s.Length > MaxDrawChars ? s.Substring(0, MaxDrawChars) + " …" : s;
        return t.IndexOf('\t') >= 0 ? t.Replace('\t', ' ') : t;
    }

    private FormattedText MakeText(string display, Brush brush, double textW)
    {
        var ft = new FormattedText(display, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, _fontSize, brush, 1.0);
        if (_wrap) ft.MaxTextWidth = textW;
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        double gutter = GutterWidth;
        dc.DrawRectangle(GutterBrush, null, new Rect(0, 0, gutter - 4, h));
        _layout.Clear();
        if (_source == null) return;

        double textW = Math.Max(10, w - gutter - 4);
        double y = 0, maxWidth = 0;
        var sel = SelectionRange;
        for (long line = _top; line < _lineCount && y < h; line++)
        {
            string? text = _source.TryGetLine(line);
            string display = text == null ? "…" : Clip(text);
            var ft = MakeText(display, text == null ? PendingBrush : Brushes.Black, textW);
            double lh = Math.Max(_lineHeight, Math.Ceiling(ft.Height + 2));
            var origin = new Point(gutter - (_wrap ? 0 : _hOffset), y + 1);
            _layout.Add(new RowLayout(line, y, lh, ft, text == null ? 0 : display.Length, origin));

            if (Current is { } c0 && c0.Line == line)
                dc.DrawRectangle(CurrentLineBrush, null, new Rect(gutter - 4, y, w, lh));

            var num = new FormattedText((line + 1).ToString("N0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                        Face, _fontSize - 1, GutterText, 1.0);
            dc.DrawText(num, new Point(gutter - 8 - num.Width, y + 1));

            dc.PushClip(new RectangleGeometry(new Rect(gutter - 2, y, w, lh)));
            if (text != null)
            {
                // 찾은 말
                if (Highlighter != null)
                    foreach (var (st, ln) in Highlighter(text))
                    {
                        if (st >= display.Length) break;
                        var geo = ft.BuildHighlightGeometry(origin, st, Math.Min(ln, display.Length - st));
                        if (geo == null) continue;
                        bool cur = Current is { } c && c.Line == line && c.Start == st;
                        dc.DrawGeometry(cur ? CurrentBrush : HitBrush, cur ? CurrentPen : null, geo);
                    }
                // 선택
                if (sel is { } s && line >= s.From.Line && line <= s.To.Line)
                {
                    int a = line == s.From.Line ? Math.Min(s.From.Col, display.Length) : 0;
                    int b = line == s.To.Line ? Math.Min(s.To.Col, display.Length) : display.Length;
                    if (b > a)
                    {
                        var geo = ft.BuildHighlightGeometry(origin, a, b - a);
                        if (geo != null) dc.DrawGeometry(SelectBrush, null, geo);
                    }
                    if (line < s.To.Line && !_wrap)   // 줄 끝(줄 바꿈)도 선택됨
                    {
                        double endX = origin.X + (display.Length == 0 ? 0 : ft.WidthIncludingTrailingWhitespace);
                        dc.DrawRectangle(SelectBrush, null, new Rect(endX, y + 1, _charWidth, lh - 2));
                    }
                }
            }
            dc.DrawText(ft, origin);
            dc.Pop();
            maxWidth = Math.Max(maxWidth, ft.WidthIncludingTrailingWhitespace);
            y += lh;
            if (text != null && !_painted) { _painted = true; Dispatcher.BeginInvoke(new Action(() => FirstPaint?.Invoke())); }
        }
        if (!_wrap && maxWidth > _maxWidth + 1)
        {
            _maxWidth = maxWidth;
            Dispatcher.BeginInvoke(new Action(UpdateBars));
        }
    }

    // ───────── 글자 위치 찾기 ─────────

    /// <summary>화면 좌표 → 글자 위치 (그린 줄 기준)</summary>
    private TextPos? HitTest(Point p)
    {
        if (_layout.Count == 0) return null;
        RowLayout row = _layout[_layout.Count - 1];
        if (p.Y < _layout[0].Y) row = _layout[0];
        else foreach (var r in _layout) if (p.Y < r.Y + r.Height) { row = r; break; }
        return new TextPos(row.Line, ColumnAt(row, p));
    }

    /// <summary>글자마다의 위치를 이분 탐색 (줄 바꿈 모드의 여러 줄도 위→아래, 왼→오 순서라 단조)</summary>
    private static int ColumnAt(RowLayout row, Point p)
    {
        int n = row.Length;
        if (n == 0) return 0;
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            var geo = row.Text.BuildHighlightGeometry(row.Origin, mid, 1);
            bool before;
            if (geo == null) before = false;
            else
            {
                var b = geo.Bounds;
                before = b.Bottom <= p.Y || (b.Top <= p.Y && b.Left + b.Width / 2 <= p.X);
            }
            if (before) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private (int Start, int End) WordAt(long line, int col)
    {
        var text = _source?.TryGetLine(line);
        if (text == null || text.Length == 0) return (col, col);
        col = Math.Max(0, Math.Min(col, text.Length - 1));
        static bool Word(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        if (!Word(text[col])) return (col, col + 1);
        int a = col, b = col;
        while (a > 0 && Word(text[a - 1])) a--;
        while (b < text.Length && Word(text[b])) b++;
        return (a, b);
    }

    // ───────── 입력 ─────────

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Shift && !_wrap) ScrollHorizontal(_hOffset - e.Delta);
        else ScrollTo(_top - e.Delta / 120 * 3);
        UserInteracted?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        UserInteracted?.Invoke();
        var pos = HitTest(e.GetPosition(this));
        if (pos is not { } p) return;
        if (e.ClickCount == 2)
        {
            var (a, b) = WordAt(p.Line, p.Col);
            _anchor = new TextPos(p.Line, a);
            _caret = new TextPos(p.Line, b);
            InvalidateVisual();
            LineActivated?.Invoke(p.Line);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Shift && _anchor != null) _caret = p;
        else _anchor = _caret = p;
        _dragging = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        var pt = e.GetPosition(this);
        if (pt.Y < 0) ScrollTo(_top - 1);
        else if (pt.Y > ActualHeight) ScrollTo(_top + 1);
        if (!_wrap)
        {
            if (pt.X < GutterWidth && _hOffset > 0) ScrollHorizontal(_hOffset - _charWidth * 4);
            else if (pt.X > ActualWidth) ScrollHorizontal(_hOffset + _charWidth * 4);
        }
        var p = HitTest(new Point(pt.X, Math.Max(0, Math.Min(ActualHeight - 1, pt.Y))));
        if (p != null) { _caret = p; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page = Math.Max(1, VisibleLines - 1);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Up: ScrollTo(_top - 1); break;
            case Key.Down: ScrollTo(_top + 1); break;
            case Key.PageUp: ScrollTo(_top - page); break;
            case Key.PageDown: ScrollTo(_top + page); break;
            case Key.Home when ctrl: ScrollTo(0); ScrollHorizontal(0); break;
            case Key.End when ctrl: ScrollTo(_lineCount); break;
            case Key.Home: ScrollHorizontal(0); break;
            case Key.End: ScrollHorizontal(_maxWidth); break;
            case Key.Left: ScrollHorizontal(_hOffset - _charWidth * 8); break;
            case Key.Right: ScrollHorizontal(_hOffset + _charWidth * 8); break;
            case Key.A when ctrl: SelectAll(); break;
            default: return;
        }
        UserInteracted?.Invoke();
        e.Handled = true;
    }
}
