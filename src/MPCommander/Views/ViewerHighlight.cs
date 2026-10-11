using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MPCommander.Views;

/// <summary>TextBlock 에 (글자, 찾은 말인지) 조각을 Run 으로 채워 찾은 말만 칠한다 (뷰어의 찾은 줄 목록용)</summary>
public static class ViewerHighlight
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(object), typeof(ViewerHighlight), new PropertyMetadata(null, OnSegmentsChanged));

    public static void SetSegments(DependencyObject d, object? value) => d.SetValue(SegmentsProperty, value);
    public static object? GetSegments(DependencyObject d) => d.GetValue(SegmentsProperty);

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not IEnumerable<(string Text, bool Hit)> segs) return;
        foreach (var (text, hit) in segs)
            tb.Inlines.Add(hit ? new Run(text) { Background = Brushes.Yellow, FontWeight = FontWeights.Bold } : new Run(text));
    }
}
