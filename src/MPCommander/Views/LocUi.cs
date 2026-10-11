using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// XAML 에 적힌 글자를 지금 언어로 바꾼다. 창·컨트롤을 만든 직후(InitializeComponent 다음)에 부른다.
/// 논리 트리를 돌며 Title, TextBlock.Text, Content, Header, ToolTip, GridView 열 머리글을 Loc.T 로 바꾼다 (바인딩은 건드리지 않음).
/// </summary>
public static class LocUi
{
    public static void Apply(DependencyObject root)
    {
        if (Loc.IsSource || root == null) return;
        Walk(root, new HashSet<DependencyObject>());
    }

    private static void Walk(DependencyObject d, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(d)) return;

        if (d is Window w && !Bound(w, Window.TitleProperty)) w.Title = Loc.T(w.Title ?? "");
        if (d is TextBlock tb && !Bound(tb, TextBlock.TextProperty) && tb.Inlines.Count <= 1 && !string.IsNullOrEmpty(tb.Text))
            tb.Text = Loc.T(tb.Text);
        if (d is HeaderedContentControl hc && hc.Header is string hs && !Bound(hc, HeaderedContentControl.HeaderProperty)) hc.Header = Loc.T(hs);
        if (d is HeaderedItemsControl hi && hi.Header is string his && !Bound(hi, HeaderedItemsControl.HeaderProperty)) hi.Header = Loc.T(his);
        if (d is ContentControl cc && cc.Content is string cs && !Bound(cc, ContentControl.ContentProperty)) cc.Content = Loc.T(cs);
        if (d is FrameworkElement fe)
        {
            if (fe.ToolTip is string tip && !Bound(fe, FrameworkElement.ToolTipProperty)) fe.ToolTip = Loc.T(tip);
            if (fe.ContextMenu != null) Walk(fe.ContextMenu, seen);
        }
        if (d is ListView lv && lv.View is GridView gv)
            foreach (var col in gv.Columns)
                if (col.Header is string ch) col.Header = Loc.T(ch);

        foreach (var child in LogicalTreeHelper.GetChildren(d))
            if (child is DependencyObject c) Walk(c, seen);
    }

    private static bool Bound(DependencyObject d, DependencyProperty p) => BindingOperations.IsDataBound(d, p);
}
