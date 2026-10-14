using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MPCommander.IO;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>Everything 결과 한 줄 (툴바 검색 상자의 펼침 목록)</summary>
public sealed class QuickResult
{
    private static readonly Brush FolderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xA0, 0x20));

    public QuickResult(EverythingItem it)
    {
        FullPath = it.FullPath;
        IsFolder = it.IsFolder;
        Name = Path.GetFileName(it.FullPath.TrimEnd('\\'));
        if (Name.Length == 0) Name = it.FullPath;
        Folder = Path.GetDirectoryName(it.FullPath.TrimEnd('\\')) ?? "";
        Size = it.IsFolder ? -1 : it.Size;
        LastWrite = it.LastWrite;
        SizeText = it.IsFolder ? "" : PathUtil.FormatSize(it.Size);
        DateText = it.LastWrite > 0 ? DateTime.FromFileTimeUtc(it.LastWrite).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    }

    public long Size { get; }        // 폴더는 -1 (크기순에서 맨 앞/뒤)
    public long LastWrite { get; }
    public string FullPath { get; }
    public bool IsFolder { get; }
    public string Name { get; }
    public string Folder { get; }
    public string SizeText { get; }
    public string DateText { get; }
    public string Glyph => IsFolder ? "" : "";
    public Brush GlyphBrush => IsFolder ? FolderBrush : Brushes.Gray;
}

/// <summary>
/// 툴바 검색 상자: 입력하는 대로(0.3초 멈추면) Everything 으로 모든 드라이브에서 찾아 아래로 펼치고,
/// Enter 면 맨 위(또는 ↑↓ 로 고른) 항목을 현재 패널에서 보여 준다.
/// Everything 이 실행 중이 아니면 Enter 는 파일 찾기 창(현재 패널 폴더 아래, 그 이름으로)을 연다.
/// </summary>
public partial class MainWindow
{
    private const int QuickMaxResults = 500;
    private DispatcherTimer? _quickTimer;
    private int _quickGeneration;
    private string? _quickResultsFor;   // 지금 목록이 어떤 말로 찾은 것인지
    private Task? _quickRunning;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!SearchBox.IsKeyboardFocusWithin) return;   // 코드에서 바꾼 경우
        if (SearchBox.Text.Trim().Length == 0)
        {
            _quickTimer?.Stop();
            _quickGeneration++;
            SearchPopup.IsOpen = false;
            return;
        }
        // 입력이 멈추면(0.3초) 찾는다 — 글자마다 찾지 않는다
        _quickTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => { _quickTimer!.Stop(); _quickRunning = QuickSearchAsync(); }, Dispatcher);
        _quickTimer.Stop();
        _quickTimer.Start();
    }

    private async void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
            {
                e.Handled = true;
                var text = SearchBox.Text.Trim();
                if (text.Length == 0) return;
                if (Everything.Version() == null)
                {
                    // Everything 이 없으면 파일 찾기 창으로
                    SearchPopup.IsOpen = false;
                    CmdSearch(text);
                    return;
                }
                // 목록이 아직 이 말로 찾은 것이 아니면 지금 찾고 나서 이동
                if (_quickTimer?.IsEnabled == true) { _quickTimer.Stop(); _quickRunning = QuickSearchAsync(); }
                if (_quickRunning != null) await _quickRunning;
                if (!string.Equals(_quickResultsFor, SearchBox.Text.Trim(), StringComparison.Ordinal)) await QuickSearchAsync();
                OpenQuickResult();
                break;
            }
            case Key.Down when SearchPopup.IsOpen && SearchResults.Items.Count > 0:
                e.Handled = true;
                MoveQuickSelection(+1);
                break;
            case Key.Up when SearchPopup.IsOpen && SearchResults.Items.Count > 0:
                e.Handled = true;
                MoveQuickSelection(-1);
                break;
            case Key.PageDown when SearchPopup.IsOpen && SearchResults.Items.Count > 0:
                e.Handled = true;
                MoveQuickSelection(+10);
                break;
            case Key.PageUp when SearchPopup.IsOpen && SearchResults.Items.Count > 0:
                e.Handled = true;
                MoveQuickSelection(-10);
                break;
            case Key.Escape:
                e.Handled = true;
                if (SearchPopup.IsOpen) SearchPopup.IsOpen = false;
                else { SearchBox.Clear(); P.FocusList(); }
                break;
        }
    }

    /// <summary>입력 칸에 커서를 둔 채 목록의 선택만 옮긴다</summary>
    private void MoveQuickSelection(int delta)
    {
        int n = SearchResults.Items.Count;
        int i = Compat.Clamp(SearchResults.SelectedIndex + delta, 0, n - 1);
        SearchResults.SelectedIndex = i;
        SearchResults.ScrollIntoView(SearchResults.Items[i]);
    }

    /// <summary>Everything 으로 찾아 펼침 목록에 채운다 (응답은 Everything 이 다른 프로세스에서 하므로 UI 는 기다리기만)</summary>
    private async Task QuickSearchAsync()
    {
        var text = SearchBox.Text.Trim();
        int gen = ++_quickGeneration;
        if (text.Length == 0) { SearchPopup.IsOpen = false; return; }
        var version = Everything.Version();
        if (version == null)
        {
            // Everything 이 없으면 펼치지 않는다 (Enter = 파일 찾기 창)
            SearchPopup.IsOpen = false;
            return;
        }
        SearchPopup.IsOpen = true;
        SearchPopupStatus.Text = Loc.T("Everything 에서 찾는 중...");
        long t0 = Compat.TickCount64;
        var items = await Everything.QueryAsync(Everything.ForSearchBox(text), QuickMaxResults, 10000);
        if (gen != _quickGeneration) return;   // 그사이 다시 찾음
        if (items == null)
        {
            SearchResults.ItemsSource = null;
            SearchPopupStatus.Text = Loc.T("Everything 이 응답하지 않습니다.");
            return;
        }
        _quickResultsFor = text;
        // Enter 는 맨 위로 가므로 순서가 중요하다: 이름이 똑같은 것 → 이름이 그 말로 시작 → 폴더 → 짧은 경로
        var list = items.Select(i => new QuickResult(i))
                        .OrderBy(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(r => r.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ThenBy(r => r.IsFolder ? 0 : 1)
                        .ThenBy(r => r.FullPath.Length)
                        .ToList();
        ResetQuickSort();
        SearchResults.ItemsSource = list;
        double secs = (Compat.TickCount64 - t0) / 1000.0;
        var count = items.Count >= QuickMaxResults ? Loc.F($"{QuickMaxResults:N0}개 이상 (앞의 {QuickMaxResults:N0}개)") : Loc.F($"{items.Count:N0}개");
        SearchPopupStatus.Text = Loc.F($"Everything {version} · {count} · {secs:0.00}초   │   Enter: 현재 패널에서 보기 · ↑↓ 고르기 · Esc: 닫기");
        if (list.Count > 0)
        {
            SearchResults.SelectedIndex = 0;
            SearchResults.ScrollIntoView(list[0]);
        }
    }

    private void SearchPopup_Opened(object? sender, EventArgs e)
    {
        // 상자 오른쪽 끝에 맞춰 왼쪽으로 펼친다 (툴바 오른쪽 끝에 있으므로)
        SearchPopup.HorizontalOffset = SearchBox.ActualWidth - SearchPopup.Width;
    }

    private void SearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                OpenQuickResult();
                break;
            case Key.Escape:
                e.Handled = true;
                SearchPopup.IsOpen = false;
                SearchBox.Focus();
                break;
            case Key.Up when SearchResults.SelectedIndex <= 0:
                e.Handled = true;
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text.Length;
                break;
        }
    }

    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 머리글 더블클릭은 정렬이지 열기가 아니다
        if (e.OriginalSource is DependencyObject d && FindParent<GridViewColumnHeader>(d) != null) return;
        OpenQuickResult();
    }

    // ── 머리글 클릭 정렬 (같은 칸을 다시 누르면 반대로) ──
    private GridViewColumn? _quickSortColumn;
    private bool _quickSortDescending;
    private readonly Dictionary<GridViewColumn, string> _quickHeaderText = new();

    private void SearchResults_HeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }) return;
        e.Handled = true;
        if (SearchResults.ItemsSource is not List<QuickResult> list || list.Count == 0) return;

        _quickSortDescending = column == _quickSortColumn && !_quickSortDescending;
        _quickSortColumn = column;

        var view = (GridView)SearchResults.View;
        int index = view.Columns.IndexOf(column);
        Func<QuickResult, object> key = index switch
        {
            1 => r => r.Folder,
            2 => r => r.Size,
            3 => r => r.LastWrite,
            _ => r => r.Name,
        };
        IComparer<object> cmp = Comparer<object>.Create((a, b) => a is string sa && b is string sb
            ? string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase)
            : Comparer<object>.Default.Compare(a, b));
        var sorted = (_quickSortDescending ? list.OrderByDescending(key, cmp) : list.OrderBy(key, cmp))
                     .ThenBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase)
                     .ToList();
        SearchResults.ItemsSource = sorted;
        SearchResults.SelectedIndex = 0;          // Enter 는 맨 위로
        SearchResults.ScrollIntoView(sorted[0]);

        // 머리글에 ▲ / ▼ (원래 글자는 번역된 것을 기억해 둔다)
        foreach (var c in view.Columns)
        {
            if (!_quickHeaderText.ContainsKey(c)) _quickHeaderText[c] = c.Header as string ?? "";
            c.Header = c == column ? _quickHeaderText[c] + (_quickSortDescending ? " ▼" : " ▲") : _quickHeaderText[c];
        }
        SearchBox.Focus();                        // 계속 입력·↑↓·Enter 할 수 있게
    }

    /// <summary>새로 찾으면 정렬 표시를 지운다 (기본 순서: 이름 같음 → 시작 → 폴더 → 짧은 경로)</summary>
    private void ResetQuickSort()
    {
        _quickSortColumn = null;
        _quickSortDescending = false;
        foreach (var kv in _quickHeaderText) kv.Key.Header = kv.Value;
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        for (var x = d; x != null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is T t) return t;
        return null;
    }

    /// <summary>고른 결과를 현재 패널에서: 파일은 그 폴더를 열고 커서를 두고, 폴더는 그 안으로</summary>
    private async void OpenQuickResult()
    {
        if (SearchResults.SelectedItem is not QuickResult r) return;
        SearchPopup.IsOpen = false;
        var p = P;
        if (r.IsFolder) await p.NavigateAsync(r.FullPath);
        else await p.NavigateAsync(r.Folder, r.Name);
        p.FocusList();
    }
}
