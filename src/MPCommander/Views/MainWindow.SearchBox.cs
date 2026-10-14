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
        SizeText = it.IsFolder ? "" : PathUtil.FormatSize(it.Size);
        DateText = it.LastWrite > 0 ? DateTime.FromFileTimeUtc(it.LastWrite).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
    }

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
/// 툴바 검색 상자: Enter = 파일 찾기 창(현재 패널 폴더 아래, 이름으로 바로 찾기),
/// Ctrl+Enter = Everything 으로 모든 드라이브에서 찾아 아래로 펼침 → 고르면 현재 패널에서 보여 준다.
/// </summary>
public partial class MainWindow
{
    private const int QuickMaxResults = 500;
    private DispatcherTimer? _quickTimer;
    private int _quickGeneration;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 펼친 상태에서는 입력이 멈추면(0.3초) 다시 찾는다
        if (!SearchPopup.IsOpen) return;
        _quickTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) => { _quickTimer!.Stop(); _ = QuickSearchAsync(); }, Dispatcher);
        _quickTimer.Stop();
        _quickTimer.Start();
    }

    private void QuickAll_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        _ = QuickSearchAsync();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Enter when mods == ModifierKeys.Control:
                e.Handled = true;
                _ = QuickSearchAsync();
                break;
            case Key.Enter when mods == ModifierKeys.None:
                e.Handled = true;
                if (SearchBox.Text.Trim().Length == 0) return;
                SearchPopup.IsOpen = false;
                CmdSearch(SearchBox.Text.Trim());
                break;
            case Key.Down when SearchPopup.IsOpen && SearchResults.Items.Count > 0:
                e.Handled = true;
                FocusResult(0);
                break;
            case Key.Escape:
                e.Handled = true;
                if (SearchPopup.IsOpen) SearchPopup.IsOpen = false;
                else { SearchBox.Clear(); P.FocusList(); }
                break;
        }
    }

    /// <summary>Everything 으로 찾아 펼침 목록에 채운다 (응답은 Everything 이 다른 프로세스에서 하므로 UI 는 기다리기만)</summary>
    private async Task QuickSearchAsync()
    {
        var text = SearchBox.Text.Trim();
        int gen = ++_quickGeneration;
        SearchPopup.IsOpen = true;
        if (text.Length == 0)
        {
            SearchResults.ItemsSource = null;
            SearchPopupStatus.Text = Loc.T("찾을 이름을 입력하세요");
            return;
        }
        var version = Everything.Version();
        if (version == null)
        {
            SearchResults.ItemsSource = null;
            SearchPopupStatus.Text = Loc.T("Everything 이 실행 중이 아닙니다. Everything(voidtools)을 실행한 뒤 다시 Ctrl+Enter 를 누르세요.");
            return;
        }
        SearchPopupStatus.Text = Loc.T("Everything 에서 찾는 중...");
        long t0 = Compat.TickCount64;
        var items = await Everything.QueryAsync(Everything.ForSearchBox(text), QuickMaxResults, 10000);
        if (gen != _quickGeneration) return;   // 그사이 다시 찾음
        if (items == null)
        {
            SearchPopupStatus.Text = Loc.T("Everything 이 응답하지 않습니다.");
            return;
        }
        // 폴더 먼저, 그다음 이름이 찾는 말로 시작하는 것, 나머지는 경로순
        var list = items.Select(i => new QuickResult(i))
                        .OrderBy(r => r.IsFolder ? 0 : 1)
                        .ThenBy(r => r.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                        .ToList();
        SearchResults.ItemsSource = list;
        double secs = (Compat.TickCount64 - t0) / 1000.0;
        var count = items.Count >= QuickMaxResults ? Loc.F($"{QuickMaxResults:N0}개 이상 (앞의 {QuickMaxResults:N0}개)") : Loc.F($"{items.Count:N0}개");
        SearchPopupStatus.Text = Loc.F($"Everything {version} · {count} · {secs:0.00}초   │   ↓ 고르기 · Enter: 현재 패널에서 보기 · Esc: 닫기");
        if (list.Count > 0) SearchResults.SelectedIndex = 0;
    }

    private void SearchPopup_Opened(object? sender, EventArgs e)
    {
        // 상자 오른쪽 끝에 맞춰 왼쪽으로 펼친다 (툴바 오른쪽 끝에 있으므로)
        SearchPopup.HorizontalOffset = SearchBox.ActualWidth - SearchPopup.Width;
    }

    private void FocusResult(int index)
    {
        SearchResults.SelectedIndex = index;
        SearchResults.UpdateLayout();
        if (SearchResults.ItemContainerGenerator.ContainerFromIndex(index) is ListViewItem item) item.Focus();
        else SearchResults.Focus();
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

    private void SearchResults_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenQuickResult();

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
