using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 마우스로 고르기 (Double Commander 방식 + 탐색기식 끌어서 고르기)
///  - 그냥 클릭: 표시한 것이 있을 때 표시 안 된 항목을 누르면 표시가 모두 사라진다. 표시된 항목을 누르면 그대로 (끌어 옮기기용)
///  - Shift+클릭: 마지막으로 누른 항목(기준)부터 거기까지. 다시 Shift+클릭하면 그 범위로 바뀐다 (기준 쪽으로 누르면 줄어든다)
///  - Ctrl+클릭: 하나씩 넣고 빼기 (기준도 그 항목으로)
///  - Ctrl+Shift+클릭 / Ctrl+Shift+↑↓ (탐색기처럼): 기준부터 거기까지를 지금 표시에 더한다 (다른 표시는 그대로)
///  - 끌어서 고르기 (탐색기처럼): 마지막 칸 오른쪽 빈 곳이나 항목 아래 빈 곳에서 끌면 사각형 안의 줄을 표시한다. Ctrl 을 누르면 더한다.
///    거기를 그냥 누르면 표시가 모두 사라진다. 칸 안에서 끌면 지금처럼 파일 끌어 놓기.
/// </summary>
public partial class FilePanel
{
    private int _anchorIndex = -1;                 // Shift+클릭 기준
    private (int From, int To)? _shiftRange;       // 마지막 Shift 범위 (다시 Shift+클릭하면 이것을 바꾼다)
    private HashSet<FileItem>? _rangeBase;         // Ctrl+Shift 범위를 시작할 때의 표시 (이 위에 범위를 더한다)
    private (int From, int To) _addRange = (0, -1);

    // 끌어서 고르기
    private bool _lassoPending, _lassoActive, _lassoClear;   // _lassoClear: 빈 곳을 그냥 눌렀다 떼면 표시를 지운다
    private bool _lassoAdd;                                  // 누른 순간 Ctrl → 지금 표시에 더한다
    private Point _lassoStart;
    private int _lassoStartIndex;
    private double _lassoStartOffset;
    private (int From, int To) _lassoRange = (0, -1);
    private HashSet<FileItem> _lassoBase = new();
    private ScrollViewer? _scroll;

    private ScrollViewer? ListScroll => _scroll ??= FindChild<ScrollViewer>(FileList);

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    /// <summary>마지막 칸 오른쪽 빈 곳을 눌렀는지 — 탐색기처럼 줄을 고르지 않고, 끌면 사각형으로 고른다</summary>
    private bool PastLastColumn(Point p) =>
        FileList.View is GridView gv && p.X + (ListScroll?.HorizontalOffset ?? 0) > gv.Columns.Sum(c => c.ActualWidth) + 6;

    /// <summary>Ctrl+Shift+↑↓: 기준부터 index 까지를 지금 표시에 더한다. 이어서 누르면 그 범위만 바뀐다 (처음 표시는 그대로)</summary>
    private void AddRangeTo(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        if (_rangeBase == null)
        {
            _rangeBase = _items.Where(i => i.IsMarked).ToHashSet();
            _addRange = (0, -1);
            if (_anchorIndex < 0 || _anchorIndex >= _items.Count) _anchorIndex = Math.Max(0, FileList.SelectedIndex);
        }
        var range = (Math.Min(_anchorIndex, index), Math.Max(_anchorIndex, index));
        for (int i = _addRange.From; i <= _addRange.To && i < _items.Count; i++)
            if (i < range.Item1 || i > range.Item2) SetMark(_items[i], _rangeBase.Contains(_items[i]));
        for (int i = range.Item1; i <= range.Item2; i++) SetMark(_items[i], true);
        _addRange = range;
        _shiftRange = null;
    }

    /// <summary>Ctrl+Shift+↑↓: 커서를 옮기며 범위를 더한다 (처음 누를 때 커서가 기준)</summary>
    private void AddRangeByKey(int delta)
    {
        if (_items.Count == 0) return;
        if (_rangeBase == null) _anchorIndex = Math.Max(0, FileList.SelectedIndex);
        MoveCursor(delta);
        AddRangeTo(FileList.SelectedIndex);
        UpdateStatus();
    }

    /// <summary>다른 키를 누르면 Ctrl+Shift 범위를 끝낸다 (다음 Ctrl+Shift 는 새 기준에서)</summary>
    private void EndRangeAdd() => _rangeBase = null;

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _lassoPending = false;
        var pos = e.GetPosition(FileList);
        if (IsOnScrollBarOrHeader(e.OriginalSource as DependencyObject)) return;
        var item = ItemFromSource(e.OriginalSource);
        if (item != null && PastLastColumn(pos)) item = null;   // 마지막 칸 오른쪽 = 빈 곳
        var mods = Keyboard.Modifiers;
        int index = item != null ? _items.IndexOf(item) : -1;

        if (item != null && mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            // 더하기만 한다: 기준부터 거기까지 표시, 이미 표시한 것은 어떤 경우에도 지우지 않는다
            EndRangeAdd();
            int from = _anchorIndex >= 0 && _anchorIndex < _items.Count ? _anchorIndex : Math.Max(0, FileList.SelectedIndex);
            for (int i = Math.Min(from, index); i <= Math.Max(from, index); i++) SetMark(_items[i], true);
            _anchorIndex = from;
            _shiftRange = null;
            UpdateStatus();
            SetCursor(index, true);
            e.Handled = true;
            return;
        }
        EndRangeAdd();

        if (item != null && mods == ModifierKeys.Control)
        {
            // Double Commander 처럼: 아직 표시한 것이 없을 때 다른 항목을 Ctrl+클릭하면 커서 항목도 함께 표시
            if (CursorItem is { } cur && cur != item && !_items.Any(i => i.IsMarked)) SetMark(cur, true);
            SetMark(item, !item.IsMarked);
            _anchorIndex = index;
            _shiftRange = null;
            UpdateStatus();
            SetCursor(index, true);
            e.Handled = true;
            return;
        }
        if (item != null && mods == ModifierKeys.Shift)
        {
            int from = _anchorIndex >= 0 && _anchorIndex < _items.Count ? _anchorIndex : Math.Max(0, FileList.SelectedIndex);
            var range = (Math.Min(from, index), Math.Max(from, index));
            // 지난 Shift 범위 중 새 범위 밖은 표시를 지운다 → 기준 쪽으로 다시 누르면 줄어든다
            if (_shiftRange is { } old)
                for (int i = old.From; i <= old.To && i < _items.Count; i++)
                    if (i < range.Item1 || i > range.Item2) SetMark(_items[i], false);
            for (int i = range.Item1; i <= range.Item2; i++) SetMark(_items[i], true);
            _shiftRange = range;
            _anchorIndex = from;
            UpdateStatus();
            SetCursor(index, true);
            e.Handled = true;
            return;
        }
        if (mods is not (ModifierKeys.None or ModifierKeys.Control) && !(item == null && mods == (ModifierKeys.Control | ModifierKeys.Shift))) return;

        // 끌어서 고르기 준비: 항목 아래나 마지막 칸 오른쪽 빈 곳 (Ctrl = 더하기 — 누른 순간의 키로 정한다)
        if (item == null)
        {
            _lassoPending = true;
            _lassoAdd = (mods & ModifierKeys.Control) != 0;
            _lassoClear = mods == ModifierKeys.None && e.ClickCount == 1;
            _lassoStart = pos;
            _lassoStartIndex = IndexAt(pos);
            _lassoStartOffset = ListScroll?.VerticalOffset ?? 0;
        }
        if (item == null || mods != ModifierKeys.None || e.ClickCount != 1)
        {
            if (item == null) e.Handled = true;
            return;
        }

        // 그냥 클릭: 표시 안 된 항목을 누르면 다른 표시를 지운다 (표시된 항목을 누르면 그대로 — 끌어 옮길 수 있게)
        if (!item.IsMarked && _items.Any(i => i.IsMarked)) ClearMarks();
        _anchorIndex = index;
        _shiftRange = null;
        _dragStart = pos;
        _dragItem = item;
    }

    private static bool IsOnScrollBarOrHeader(DependencyObject? d)
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is System.Windows.Controls.Primitives.ScrollBar or GridViewColumnHeader) return true;
        return false;
    }

    /// <summary>점 아래 줄 번호. 항목이 없으면 위쪽은 처음 보이는 줄, 아래쪽은 마지막 줄</summary>
    private int IndexAt(Point p)
    {
        if (_items.Count == 0) return -1;
        if (FileList.InputHitTest(p) is DependencyObject d && ItemFromSource(d) is { } it) return _items.IndexOf(it);
        var sv = ListScroll;
        int first = sv != null ? (int)sv.VerticalOffset : 0;
        int last = sv != null ? Math.Min(_items.Count - 1, (int)(sv.VerticalOffset + sv.ViewportHeight)) : _items.Count - 1;
        return p.Y < RowsTop() ? first : last;
    }

    /// <summary>목록에서 줄이 시작하는 높이 (머리글 아래)</summary>
    private double RowsTop()
    {
        if (FileList.ItemContainerGenerator.ContainerFromIndex((int)(ListScroll?.VerticalOffset ?? 0)) is FrameworkElement c)
            return c.TranslatePoint(new Point(0, 0), FileList).Y;
        return 24;
    }

    private double RowHeight()
    {
        if (FileList.ItemContainerGenerator.ContainerFromIndex((int)(ListScroll?.VerticalOffset ?? 0)) is FrameworkElement c && c.ActualHeight > 0)
            return c.ActualHeight;
        return 20;
    }

    private void FileList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        bool clickedEmpty = _lassoPending && !_lassoActive && _lassoClear;
        _lassoPending = false;
        if (clickedEmpty && _items.Any(i => i.IsMarked)) ClearMarks();
        if (!_lassoActive) return;
        _lassoActive = false;
        FileList.ReleaseMouseCapture();
        LassoRect.Visibility = Visibility.Collapsed;
        int cur = IndexAt(e.GetPosition(FileList));
        if (cur >= 0) SetCursor(cur, true);
        UpdateStatus();
        e.Handled = true;
    }

    /// <summary>끌어서 고르기 진행 (PreviewMouseMove 에서). 처리했으면 true</summary>
    private bool LassoMove(MouseEventArgs e)
    {
        if (!_lassoPending && !_lassoActive) return false;
        if (e.LeftButton != MouseButtonState.Pressed) { _lassoPending = false; return false; }
        var pos = e.GetPosition(FileList);
        if (!_lassoActive)
        {
            if (Math.Abs(pos.X - _lassoStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _lassoStart.Y) < SystemParameters.MinimumVerticalDragDistance) return true;
            if (_items.Count == 0 || _lassoStartIndex < 0) { _lassoPending = false; return true; }
            _dragItem = null;
            // 끄는 동안 Ctrl 을 떼어도 지우지 않는다 (누른 순간 Ctrl 이었으면 더하기)
            _lassoBase = _lassoAdd ? _items.Where(i => i.IsMarked).ToHashSet() : new HashSet<FileItem>();
            if (!_lassoAdd) ClearMarks();
            _lassoRange = (0, -1);
            _shiftRange = null;
            _rangeBase = null;
            _anchorIndex = _lassoStartIndex;
            _lassoActive = true;
            // 상태를 다 정한 뒤에 잡는다: CaptureMouse 는 그 자리에서 MouseMove 를 한 번 더 일으켜 이 함수로 다시 들어온다
            // (먼저 잡으면 지난번 끌기의 범위·기준으로 되돌려 이전 표시가 풀렸다)
            FileList.CaptureMouse();
        }

        // 가장자리에 닿으면 굴린다
        var sv = ListScroll;
        if (sv != null)
        {
            if (pos.Y < RowsTop()) sv.LineUp();
            else if (pos.Y > FileList.ActualHeight - 4) sv.LineDown();
        }
        int cur = IndexAt(pos);
        if (cur < 0) return true;
        var range = (Math.Min(_lassoStartIndex, cur), Math.Max(_lassoStartIndex, cur));        // 바뀐 부분만: 빠진 줄은 원래대로, 든 줄은 표시 — Ctrl 이면 탐색기처럼 반전 (이미 표시된 것은 풀린다)
        for (int i = _lassoRange.From; i <= _lassoRange.To && i < _items.Count; i++)
            if (i < range.Item1 || i > range.Item2) SetMark(_items[i], _lassoBase.Contains(_items[i]));
        for (int i = range.Item1; i <= range.Item2; i++) SetMark(_items[i], !_lassoBase.Contains(_items[i]));
        _lassoRange = range;
        UpdateStatus();

        // 사각형: 시작점은 굴린 만큼 따라 움직인다
        double shift = ((sv?.VerticalOffset ?? 0) - _lassoStartOffset) * RowHeight();
        double y0 = Compat.Clamp(_lassoStart.Y - shift, 0, FileList.ActualHeight);
        double y1 = Compat.Clamp(pos.Y, 0, FileList.ActualHeight);
        double x0 = Compat.Clamp(Math.Min(_lassoStart.X, pos.X), 0, FileList.ActualWidth);
        double x1 = Compat.Clamp(Math.Max(_lassoStart.X, pos.X), 0, FileList.ActualWidth);
        Canvas.SetLeft(LassoRect, x0);
        Canvas.SetTop(LassoRect, Math.Min(y0, y1));
        LassoRect.Width = Math.Max(1, x1 - x0);
        LassoRect.Height = Math.Max(1, Math.Abs(y1 - y0));
        LassoRect.Visibility = Visibility.Visible;
        return true;
    }
}
