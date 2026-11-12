using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MPCommander.Views;

/// <summary>뷰어의 찾은 줄 하나</summary>
internal sealed class LineHit
{
    public long Line { get; init; }               // 0부터
    public int Count { get; init; }
    public long FirstMatch { get; set; }          // 파일 전체에서 이 줄 첫 일치의 순번
    public string Preview { get; init; } = "";
    public List<(int Off, int Len)> Offsets { get; } = new();
}

/// <summary>
/// 찾은 줄 목록 (직접 그리는 가상 목록). 수십만 줄을 붙여도 보이는 행만 그리므로
/// 찾는 중에 목록이 다시 만들어지거나 깜박이지 않고, 선택·스크롤 위치도 그대로다.
/// 여러 줄 선택: Ctrl+클릭 하나씩, Shift+클릭·Shift+↑↓ 범위, 끌기, Ctrl+A 모두 (복사는 뷰어가 한다).
/// </summary>
internal sealed class HitListView : FrameworkElement
{
    private static readonly Typeface Face = new(new FontFamily("Consolas, D2Coding, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface HeaderFace = new(new FontFamily("Segoe UI, Malgun Gothic"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Brush HeaderBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3)));
    private static readonly Pen HeaderPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)), 1));
    private static readonly Brush GrayText = Frozen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x60)));
    private static readonly Brush HitBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0x40)));
    private static readonly Brush SelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xFF)));
    private static readonly Brush SelInactiveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)));
    private static readonly Pen CursorPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromRgb(0x33, 0x77, 0xDD)), 1));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

    private IReadOnlyList<LineHit> _items = Array.Empty<LineHit>();
    private int _top;
    private int _selected = -1;                   // 커서 (본문이 따라가는 줄)
    private int _anchor = -1;                     // Shift 범위의 기준
    private readonly HashSet<int> _sel = new();   // 선택한 행들
    private const double BaseFontSz = 12;
    private double FontSz = BaseFontSz;
    private double _rowH;

    public ScrollBar? VBar { get; set; }
    /// <summary>사용자가 행을 골랐다 (클릭·키보드)</summary>
    public event Action<int>? RowChosen;

    public HitListView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        MeasureRow();
    }

    private void MeasureRow()
    {
        var ft = new FormattedText(Loc.T("0가"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSz, Brushes.Black, 1.0);
        _rowH = Math.Ceiling(ft.Height + 4);
    }

    /// <summary>글자 확대 비율 (Ctrl+휠, 1 = 100%)</summary>
    public double Zoom
    {
        get => FontSz / BaseFontSz;
        set { FontSz = BaseFontSz * value; MeasureRow(); Refresh(); InvalidateVisual(); }
    }

    /// <summary>목록 원본 (끝에 덧붙이기만 하는 리스트). 바뀌면 Refresh 만 부르면 된다.</summary>
    public IReadOnlyList<LineHit> Items
    {
        get => _items;
        set { _items = value; _top = 0; _selected = -1; _anchor = -1; _sel.Clear(); Refresh(); }
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            // 프로그램이 고른 줄 (F3 다음 찾기 등): 여러 줄 선택은 풀고 그 줄 하나로
            _selected = value; _anchor = value; _sel.Clear();
            if (value >= 0) _sel.Add(value);
            EnsureVisible(value); InvalidateVisual();
        }
    }

    /// <summary>선택한 행 번호 (위에서부터)</summary>
    public List<int> SelectedRows()
    {
        var rows = _sel.Where(i => i < _items.Count).ToList();
        rows.Sort();
        return rows;
    }

    public void SelectAll()
    {
        _sel.Clear();
        for (int i = 0; i < _items.Count; i++) _sel.Add(i);
        InvalidateVisual();
    }

    private double HeaderH => _rowH + 2;
    private int VisibleRows => Math.Max(1, (int)((ActualHeight - HeaderH) / _rowH));

    /// <summary>항목 수가 늘었을 때: 스크롤 막대와 화면만 갱신 (선택·스크롤 위치 유지)</summary>
    public void Refresh()
    {
        if (_sel.Count > 0) _sel.RemoveWhere(i => i >= _items.Count);   // 끝의 줄이 빠졌을 수 있다 (파일 새로 고침)
        UpdateBar();
        InvalidateVisual();
    }

    private void UpdateBar()
    {
        if (VBar == null) return;
        VBar.Minimum = 0;
        VBar.Maximum = Math.Max(0, _items.Count - VisibleRows);
        VBar.ViewportSize = VisibleRows;
        VBar.LargeChange = Math.Max(1, VisibleRows - 1);
        VBar.SmallChange = 1;
        VBar.Value = _top;
    }

    public void ScrollTo(int top)
    {
        _top = Math.Max(0, Math.Min(Math.Max(0, _items.Count - VisibleRows), top));
        UpdateBar();
        InvalidateVisual();
    }

    public int TopRow => _top;

    private void EnsureVisible(int i)
    {
        if (i < 0) return;
        if (i < _top) ScrollTo(i);
        else if (i >= _top + VisibleRows) ScrollTo(i - VisibleRows + 1);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateBar();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
        double colLine = 84 * Zoom, colCount = 40 * Zoom;   // 줄 번호·개수 칸도 글자 크기를 따라
        // 머리글
        dc.DrawRectangle(HeaderBrush, null, new Rect(0, 0, w, HeaderH));
        dc.DrawLine(HeaderPen, new Point(0, HeaderH - 0.5), new Point(w, HeaderH - 0.5));
        DrawHeader(dc, Loc.T("줄"), 0, colLine);
        DrawHeader(dc, Loc.T("개"), colLine, colCount);
        DrawHeader(dc, Loc.T("내용"), colLine + colCount, w - colLine - colCount);

        bool focused = IsKeyboardFocusWithin;
        double y = HeaderH;
        int count = _items.Count;
        for (int i = _top; i < count && y < h; i++)
        {
            var it = _items[i];
            if (_sel.Contains(i)) dc.DrawRectangle(focused ? SelBrush : SelInactiveBrush, null, new Rect(0, y, w, _rowH));
            if (i == _selected && focused && _sel.Count > 1) dc.DrawRectangle(null, CursorPen, new Rect(0.5, y + 0.5, w - 1, _rowH - 1));

            var num = Text((it.Line + 1).ToString("N0", CultureInfo.InvariantCulture), GrayText);
            dc.DrawText(num, new Point(colLine - 8 - num.Width, y + 2));
            if (it.Count > 1) dc.DrawText(Text(it.Count.ToString(CultureInfo.InvariantCulture), GrayText), new Point(colLine + 6, y + 2));

            var ft = Text(it.Preview, TextBrush);
            ft.MaxTextWidth = Math.Max(10, w - colLine - colCount - 8);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            var origin = new Point(colLine + colCount, y + 2);
            foreach (var (off, len) in it.Offsets)
            {
                if (off >= it.Preview.Length) break;
                var geo = ft.BuildHighlightGeometry(origin, off, Math.Min(len, it.Preview.Length - off));
                if (geo != null) dc.DrawGeometry(HitBrush, null, geo);
            }
            dc.DrawText(ft, origin);
            y += _rowH;
        }
    }

    private void DrawHeader(DrawingContext dc, string text, double x, double width)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, HeaderFace, 12, Brushes.Black, 1.0);
        dc.DrawText(ft, new Point(x + Math.Max(4, (width - ft.Width) / 2), 1));
        if (x > 0) dc.DrawLine(HeaderPen, new Point(x - 0.5, 2), new Point(x - 0.5, HeaderH - 2));
    }

    private FormattedText Text(string s, Brush b)
        => new(s.Length > 600 ? s.Substring(0, 600) : s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, FontSz, b, 1.0);

    // ───────── 입력 ─────────

    /// <summary>행 고르기. Shift = 기준부터 범위 (Ctrl+Shift 는 지금 선택에 더함), Ctrl = 하나 넣고 빼기</summary>
    private void Choose(int i, ModifierKeys mods = ModifierKeys.None)
    {
        if (_items.Count == 0) return;
        i = Math.Max(0, Math.Min(_items.Count - 1, i));
        bool shift = (mods & ModifierKeys.Shift) != 0, ctrl = (mods & ModifierKeys.Control) != 0;
        if (shift)
        {
            if (_anchor < 0 || _anchor >= _items.Count) _anchor = _selected >= 0 ? _selected : i;
            if (!ctrl) _sel.Clear();
            for (int k = Math.Min(_anchor, i); k <= Math.Max(_anchor, i); k++) _sel.Add(k);
        }
        else if (ctrl)
        {
            if (!_sel.Remove(i)) _sel.Add(i);
            _anchor = i;
        }
        else
        {
            _sel.Clear(); _sel.Add(i);
            _anchor = i;
        }
        _selected = i;
        EnsureVisible(i);
        InvalidateVisual();
        RowChosen?.Invoke(i);
    }

    private int RowAt(double y) => y < HeaderH ? -1 : _top + (int)((y - HeaderH) / _rowH);

    private bool _dragging;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        int i = RowAt(e.GetPosition(this).Y);
        if (i < 0) return;
        if (i < _items.Count)
        {
            Choose(i, Keyboard.Modifiers);
            // 끌어서 범위 선택 (Ctrl 로 넣고 뺀 경우는 끌지 않는다)
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) { _dragging = true; CaptureMouse(); }
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        double y = e.GetPosition(this).Y;
        // 위·아래 끝을 넘으면 굴리면서 넓힌다
        if (y < HeaderH) ScrollTo(_top - 1);
        else if (y > ActualHeight) ScrollTo(_top + 1);
        int i = Math.Max(0, Math.Min(_items.Count - 1, RowAt(Math.Max(HeaderH, Math.Min(ActualHeight - 1, y)))));
        if (i != _selected) Choose(i, ModifierKeys.Shift);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging) { _dragging = false; ReleaseMouseCapture(); }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        // 선택 밖을 누르면 그 줄 하나로 (선택 안이면 그대로 두고 메뉴만)
        Focus();
        int i = RowAt(e.GetPosition(this).Y);
        if (i >= 0 && i < _items.Count && !_sel.Contains(i)) Choose(i);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ScrollTo(_top - e.Delta / 120 * 3);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int page = Math.Max(1, VisibleRows - 1);
        int cur = _selected < 0 ? _top - 1 : _selected;
        var mods = Keyboard.Modifiers & ModifierKeys.Shift;   // Shift+↑↓ 는 범위 넓히기
        switch (e.Key)
        {
            case Key.Down: Choose(cur + 1, mods); break;
            case Key.Up: Choose(Math.Max(0, cur - 1), mods); break;
            case Key.PageDown: Choose(cur + page, mods); break;
            case Key.PageUp: Choose(cur - page, mods); break;
            case Key.Home: Choose(0, mods); break;
            case Key.End: Choose(_items.Count - 1, mods); break;
            default: return;
        }
        e.Handled = true;
    }
}
