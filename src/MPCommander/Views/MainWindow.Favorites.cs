using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 즐겨찾기 바 (툴바 아래): 패널의 ☆ 로 올린 폴더를 브라우저 즐겨찾기처럼 보여 준다.
/// 누르면 활성 패널에서 열고 (Ctrl+클릭은 대상 패널), 오른쪽 버튼으로 이름·경로 고치기 / 순서 / 빼기.
/// 이름이 없으면 경로만, 있으면 "이름 (경로)".
/// </summary>
public partial class MainWindow
{
    private void InitFavoritesBar()
    {
        _history.Changed += () => Dispatcher.BeginInvoke(new Action(RebuildFavoritesBar));
        ApplyFavoritesBarVisibility();
        RebuildFavoritesBar();
    }

    private void ApplyFavoritesBarVisibility()
    {
        FavBar.Visibility = _settings.ShowFavoritesBar ? Visibility.Visible : Visibility.Collapsed;
        FavBarToggle.IsChecked = _settings.ShowFavoritesBar;
        FavBarMenuItem.IsChecked = _settings.ShowFavoritesBar;
    }

    private void FavBar_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowFavoritesBar = FavBarToggle.IsChecked == true;
        ApplyFavoritesBarVisibility();
        P.FocusList();
    }

    private void FavBarMenu_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowFavoritesBar = FavBarMenuItem.IsChecked;
        ApplyFavoritesBarVisibility();
    }

    public static string FavoriteText(string path, string label) => label.Length > 0 ? $"{label} ({path})" : path;

    private void RebuildFavoritesBar()
    {
        FavItems.Children.Clear();
        var favs = _history.Favorites;
        foreach (var (path, label) in favs)
        {
            var text = new TextBlock { Text = FavoriteText(path, label), MaxWidth = 380, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 0, 5, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xD8, 0xA0, 0x20)), VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(text);
            var b = new Button
            {
                Content = content,
                Tag = path,
                ToolTip = Loc.F($"{path}\n누르기: 활성 패널에서 열기 · Ctrl+누르기: 대상 패널 · 오른쪽 버튼: 고치기·순서·빼기"),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 1, 2, 1),
                Focusable = false,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
            };
            b.Click += (_, _) => OpenFavorite(path, Keyboard.Modifiers == ModifierKeys.Control);
            b.ContextMenu = FavoriteMenu(path, label);
            FavItems.Children.Add(b);
        }
        // 지금 폴더 더하기 (패널의 ☆ 와 같다)
        var add = new Button
        {
            Content = favs.Count == 0 ? Loc.T("☆ 지금 폴더를 즐겨찾기에 (패널의 ☆ 또는 Ctrl+D)") : "＋",
            ToolTip = Loc.T("활성 패널의 지금 폴더를 즐겨찾기 바에 더하기"),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 1, 2, 1),
            Focusable = false,
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = Brushes.Gray,
        };
        add.Click += (_, _) =>
        {
            if (P.CurrentPath is { } cur) _history.AddFavorite(cur);
            P.FocusList();
        };
        FavItems.Children.Add(add);
    }

    private async void OpenFavorite(string path, bool inTarget)
    {
        var p = inTarget && _target != null ? _target : P;
        await p.NavigateAsync(path);
        P.FocusList();
    }

    private ContextMenu FavoriteMenu(string path, string label)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action act)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
            return mi;
        }
        Item(Loc.T("활성 패널에서 열기"), () => OpenFavorite(path, false));
        Item(Loc.T("대상 패널에서 열기"), () => OpenFavorite(path, true));
        menu.Items.Add(new Separator());
        Item(Loc.T("이름 · 경로 고치기..."), () => EditFavorite(path, label));
        Item(Loc.T("◀ 왼쪽으로"), () => _history.MoveFavorite(path, -1));
        Item(Loc.T("오른쪽으로 ▶"), () => _history.MoveFavorite(path, +1));
        menu.Items.Add(new Separator());
        Item(Loc.T("즐겨찾기에서 빼기"), () => _history.RemoveFavorite(path));
        return menu;
    }

    private void EditFavorite(string path, string label)
    {
        var r = FavoriteEditDialog.Show(this, path, label);
        if (r is not { } edit) return;
        _history.EditFavorite(path, edit.Path, edit.Label);
        _history.Save();
    }
}
