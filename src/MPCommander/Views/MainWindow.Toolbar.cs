using System.Windows;
using System.Windows.Controls;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 툴바 구성: XAML 의 항목마다 Uid 가 이름이고, 설정(ToolbarItems)의 순서대로 다시 놓는다. "|" = 구분선.
/// 오른쪽 버튼 → '툴바 사용자 지정' 에서 항목을 넣고 빼고 순서를 바꾼다.
/// </summary>
public partial class MainWindow
{
    /// <summary>기본 구성: 패널 배치 | 뒤로 · 앞으로 · 위 · 새로고침 | 자주 쓰는 작업 | 즐겨찾기 바 · 경로 복사 · 원격 · MDNotePad+ · 설명서 · 설정</summary>
    public static readonly string[] DefaultToolbar =
    [
        "layout2", "layout3", "layout6", "layout8", "|",
        "back", "forward", "up", "refresh", "|",
        "search", "pack", "unpack", "unblock", "backup", "restore", "foldersync", "|",
        "favbar", "copyfullpaths", "connections", "mdsync", "manual", "settings",
    ];

    private readonly Dictionary<string, List<UIElement>> _toolItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _toolOrderAll = new();   // XAML 에 있는 순서 (사용자 지정 창의 '쓸 수 있는 항목' 순서)

    /// <summary>XAML 의 툴바 항목을 Uid 로 모은 뒤 설정 순서로 다시 놓는다 (창을 만들 때 한 번)</summary>
    private void InitToolbar()
    {
        foreach (var el in MainToolBar.Items.OfType<UIElement>().ToList())
        {
            if (string.IsNullOrEmpty(el.Uid)) continue;
            if (!_toolItems.TryGetValue(el.Uid, out var list)) { _toolItems[el.Uid] = list = new List<UIElement>(); _toolOrderAll.Add(el.Uid); }
            list.Add(el);
        }
        ApplyToolbar();
    }

    /// <summary>지금 툴바 순서 (설정이 비었거나 알 수 없는 것만 있으면 기본)</summary>
    public List<string> ToolbarOrder()
    {
        var ids = _settings.ToolbarItems.SplitTrim(',')
            .SelectMany(id => id == "layouts" ? ["layout2", "layout3", "layout4", "layout6", "layout8"] : new[] { id })   // 예전 설정: 배치 묶음
            .Where(id => id == "|" || _toolItems.ContainsKey(id)).ToList();
        return ids.Any(id => id != "|") ? ids : DefaultToolbar.ToList();
    }

    private void ApplyToolbar()
    {
        MainToolBar.Items.Clear();
        bool lastSeparator = true;   // 맨 앞·연달은 구분선은 넣지 않는다
        foreach (var id in ToolbarOrder())
        {
            if (id == "|")
            {
                if (!lastSeparator) { MainToolBar.Items.Add(new Separator()); lastSeparator = true; }
                continue;
            }
            if (!_toolItems.TryGetValue(id, out var els)) continue;
            foreach (var el in els) MainToolBar.Items.Add(el);
            lastSeparator = false;
        }
        if (MainToolBar.Items.Count > 0 && MainToolBar.Items[MainToolBar.Items.Count - 1] is Separator)
            MainToolBar.Items.RemoveAt(MainToolBar.Items.Count - 1);
    }

    /// <summary>항목 이름 (사용자 지정 창): 도구 설명의 첫 부분</summary>
    public string ToolbarItemName(string id)
    {
        if (!_toolItems.TryGetValue(id, out var els) || els[0] is not FrameworkElement fe) return id;
        var tip = fe.ToolTip as string ?? id;
        var cut = tip.IndexOfAny(['\n', '—']);
        return (cut > 0 ? tip.Substring(0, cut) : tip).Trim();
    }

    private void CustomizeToolbar_Click(object sender, RoutedEventArgs e) => CmdCustomizeToolbar();

    public void CmdCustomizeToolbar()
    {
        var result = ToolbarCustomizeWindow.Show(this, ToolbarOrder(), _toolOrderAll, DefaultToolbar);
        if (result == null) return;
        _settings.ToolbarItems = result.SequenceEqual(DefaultToolbar) ? "" : string.Join(",", result);
        SaveSettings();
        ApplyToolbar();
    }
}
