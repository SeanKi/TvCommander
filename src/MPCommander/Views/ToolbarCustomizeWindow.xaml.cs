using System.Collections.ObjectModel;
using System.Windows;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>툴바 사용자 지정: 넣을 항목과 순서, 구분선. 결과는 Uid 목록 ("|" = 구분선)</summary>
public partial class ToolbarCustomizeWindow : Window
{
    public sealed record Entry(string Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly ObservableCollection<Entry> _available = new();
    private readonly ObservableCollection<Entry> _toolbar = new();
    private readonly Func<string, string> _name;
    private readonly IReadOnlyList<string> _all;
    private readonly IReadOnlyList<string> _defaults;
    private List<string>? _result;

    private ToolbarCustomizeWindow(IReadOnlyList<string> current, IReadOnlyList<string> all, IReadOnlyList<string> defaults, Func<string, string> name)
    {
        InitializeComponent();
        LocUi.Apply(this);   // XAML 글자를 지금 언어로
        _name = name;
        _all = all;
        _defaults = defaults;
        AvailableList.ItemsSource = _available;
        ToolbarList.ItemsSource = _toolbar;
        Fill(current);
    }

    public static List<string>? Show(MainWindow owner, IReadOnlyList<string> current, IReadOnlyList<string> all, IReadOnlyList<string> defaults)
    {
        var w = new ToolbarCustomizeWindow(current, all, defaults, owner.ToolbarItemName) { Owner = owner };
        return w.ShowDialog() == true ? w._result : null;
    }

    private Entry Make(string id) => id == "|" ? new Entry("|", Loc.T("──── 구분선 ────")) : new Entry(id, _name(id));

    private void Fill(IReadOnlyList<string> ids)
    {
        _toolbar.Clear();
        foreach (var id in ids) _toolbar.Add(Make(id));
        RefreshAvailable();
    }

    private void RefreshAvailable()
    {
        _available.Clear();
        foreach (var id in _all.Where(a => !_toolbar.Any(t => t.Id == a))) _available.Add(Make(id));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var sel = AvailableList.SelectedItems.Cast<Entry>().ToList();
        if (sel.Count == 0) return;
        int at = ToolbarList.SelectedIndex >= 0 ? ToolbarList.SelectedIndex + 1 : _toolbar.Count;
        foreach (var en in sel) _toolbar.Insert(at++, en);
        RefreshAvailable();
        ToolbarList.SelectedIndex = at - 1;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var sel = ToolbarList.SelectedItems.Cast<Entry>().ToList();
        foreach (var en in sel) _toolbar.Remove(en);
        RefreshAvailable();
    }

    private void Separator_Click(object sender, RoutedEventArgs e)
    {
        int at = ToolbarList.SelectedIndex >= 0 ? ToolbarList.SelectedIndex + 1 : _toolbar.Count;
        _toolbar.Insert(at, Make("|"));
        ToolbarList.SelectedIndex = at;
    }

    private void Move(int delta)
    {
        int i = ToolbarList.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _toolbar.Count) return;
        _toolbar.Move(i, j);
        ToolbarList.SelectedIndex = j;
        ToolbarList.ScrollIntoView(_toolbar[j]);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(+1);
    private void Default_Click(object sender, RoutedEventArgs e) => Fill(_defaults);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _result = _toolbar.Select(t => t.Id).ToList();
        DialogResult = true;
    }
}
