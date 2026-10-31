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
///  - 끌어서 고르기: 이름 칸 밖(크기·날짜 칸)이나 항목 아래 빈 곳에서 끌면 사각형 안의 줄을 표시한다. Ctrl 을 누르면 더한다.
///    이름 칸에서 끌면 지금처럼 파일 끌어 놓기.
/// </summary>
public partial class FilePanel
{
    private int _anchorIndex = -1;                 // Shift+클릭 기준
    private (int From, int To)? _shiftRange;       // 마지막 Shift 범위 (다시 Shift+클릭하면 이것을 바꾼다)

    // 끌어서 고르기
    private bool _lassoPending, _lassoActive;
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

    /// <summary>이름 칸 밖(크기·날짜…)을 눌렀는지 — 거기서 끌면 사각형으로 고른다</summary>
    private bool OutsideNameColumn(Point p) => p.X > NameColumn.ActualWidth + 6;

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _lassoPending = false;
        var pos = e.GetPosition(FileList);
        if (IsOnScrollBarOrHeader(e.OriginalSource as DependencyObject)) return;
        var item = ItemFromSource(e.OriginalSource);
        var mods = Keyboard.Modifiers;
        int index = item != null ? _items.IndexOf(item) : -1;

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
        if (mods is not (ModifierKeys.None or ModifierKeys.Control)) return;

        // 끌어서 고르기 준비: 항목 아래 빈 곳, 또는 이름 칸 밖 (Ctrl = 더하기)
        if (item == null || OutsideNameColumn(pos))
        {
            _lassoPending = true;
            _lassoStart = pos;
            _lassoStartIndex = item != null ? index : IndexAt(pos);
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
        if (!_lassoPending)
        {
            _dragStart = pos;
            _dragItem = item;
        }
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
        _lassoPending = false;
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
            _lassoActive = true;
            _dragItem = null;
            FileList.CaptureMouse();
            _lassoBase = Keyboard.Modifiers == ModifierKeys.Control ? _items.Where(i => i.IsMarked).ToHashSet() : new HashSet<FileItem>();
            if (Keyboard.Modifiers != ModifierKeys.Control) ClearMarks();
            _lassoRange = (0, -1);
            _shiftRange = null;
            _anchorIndex = _lassoStartIndex;
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
        var range = (Math.Min(_lassoStartIndex, cur), Math.Max(_lassoStartIndex, cur));
        // 바뀐 부분만: 빠진 줄은 원래대로, 새로 든 줄은 표시
        for (int i = _lassoRange.From; i <= _lassoRange.To && i < _items.Count; i++)
            if (i < range.Item1 || i > range.Item2) SetMark(_items[i], _lassoBase.Contains(_items[i]));
        for (int i = range.Item1; i <= range.Item2; i++) SetMark(_items[i], true);
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
