using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MPCommander.Views;

/// <summary>툴바 아이콘과 목록 글씨 크기</summary>
public partial class MainWindow
{
    /// <summary>목록 글씨 크기 3단계 (px). 보통 14px ≈ 10.5pt 로 Double Commander 기본과 비슷하다.</summary>
    private static readonly (string Name, double Size)[] FontLevels = [(Loc.T("작게"), 12), (Loc.T("보통"), 14), (Loc.T("크게"), 16)];

    private void InitToolbarIcons()
    {
        Layout2.Content = LayoutIcon(1, 2);
        Layout3.Content = LayoutIcon(1, 3);
        Layout4.Content = LayoutIcon(2, 2);
        Layout6.Content = LayoutIcon(2, 3);
        Layout8.Content = LayoutIcon(2, 4);
    }

    /// <summary>패널 배치 아이콘: 행 × 열 칸을 그린 작은 격자</summary>
    private static FrameworkElement LayoutIcon(int rows, int cols)
    {
        var grid = new UniformGrid { Rows = rows, Columns = cols, Width = 24, Height = 16, Margin = new Thickness(1) };
        for (int i = 0; i < rows * cols; i++)
        {
            grid.Children.Add(new Border
            {
                BorderBrush = SystemColors.ControlDarkDarkBrush,
                BorderThickness = new Thickness(1.2),
                Background = SystemColors.WindowBrush,
                Margin = new Thickness(0.7),
                CornerRadius = new CornerRadius(1),
            });
        }
        return grid;
    }

    /// <summary>목록 글씨 크기 적용 (모든 패널과 찾기 결과 목록이 DynamicResource 로 따라온다)</summary>
    private void ApplyFontLevel(int level)
    {
        level = Compat.Clamp(level, 0, FontLevels.Length - 1);
        _settings.FontLevel = level;
        _settings.ZoomList = ViewZoom.Clamp(_settings.ZoomList);
        // 크기 단계(작게·보통·크게) × Ctrl+휠 확대 비율
        Application.Current.Resources["ListFontSize"] = Math.Round(FontLevels[level].Size * _settings.ZoomList / 100.0, 1);
        FontButton.ToolTip = Loc.F($"목록 글씨 크기: {FontLevels[level].Name} (Ctrl+= / Ctrl+-)") +
                             (_settings.ZoomList != 100 ? Loc.F($", 확대 {_settings.ZoomList}% (Ctrl+휠, Ctrl+0 = 100%)") : "");
    }

    /// <summary>목록(패널·찾기 결과·폴더 비교) Ctrl+휠 확대 비율</summary>
    public void SetListZoom(int percent)
    {
        _settings.ZoomList = ViewZoom.Clamp(percent);
        ApplyFontLevel(_settings.FontLevel);
    }

    public int ListZoom => _settings.ZoomList;

    private void Font_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FontButton, Placement = PlacementMode.Bottom };
        for (int i = 0; i < FontLevels.Length; i++)
        {
            int level = i;
            var item = new MenuItem
            {
                Header = $"{FontLevels[i].Name} ({FontLevels[i].Size:0}px)",
                IsCheckable = true,
                IsChecked = _settings.FontLevel == i,
                FontSize = FontLevels[i].Size,
            };
            item.Click += (_, _) => { ApplyFontLevel(level); P.FocusList(); };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => P.FocusList();
        menu.IsOpen = true;
    }
}
