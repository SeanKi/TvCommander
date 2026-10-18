using System.Windows;
using System.Windows.Input;
using MPCommander.Model;

namespace MPCommander.Views;

/// <summary>
/// 보기 창 공통 확대·축소: Ctrl+휠 (10% 씩), Ctrl+0 = 100%.
/// 범위는 설정의 ZoomMin ~ ZoomMax (기본 75% ~ 200%, settings.ini 에서 바꿀 수 있다).
/// 마지막 비율은 창 종류별로 설정에 남는다.
/// </summary>
public static class ViewZoom
{
    /// <summary>MainWindow 가 시작할 때 넣는다 (범위와 마지막 비율을 읽고 쓴다)</summary>
    public static AppSettings? Settings { get; set; }

    public const int Step = 10;

    public static int Min => Math.Max(10, Math.Min(Settings?.ZoomMin ?? 75, Settings?.ZoomMax ?? 200));
    public static int Max => Math.Max(Min, Settings?.ZoomMax ?? 200);

    public static int Clamp(int percent) => Compat.Clamp(percent, Min, Max);

    /// <summary>
    /// target 위에서 Ctrl+휠·Ctrl+0 으로 비율을 바꾼다. apply(비율 %) 가 실제로 글자·배율을 바꾸고,
    /// report 는 바뀐 비율을 잠깐 보여 줄 곳 (상태 줄 등).
    /// </summary>
    public static void Attach(UIElement target, Func<int> get, Action<int> apply, Action<string>? report = null, UIElement? keyTarget = null)
    {
        target.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;   // 아래 목록·편집기가 스크롤하지 않도록
            int now = get();
            // 10% 단위에 맞춰 한 칸씩 (예: 95% → 100%, 105% → 100%)
            int next = e.Delta > 0 ? (now / Step + 1) * Step : ((now + Step - 1) / Step - 1) * Step;
            Set(Clamp(next));
        };
        (keyTarget ?? target).PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D0 or Key.NumPad0)
            {
                e.Handled = true;
                Set(Clamp(100));
            }
        };

        void Set(int percent)
        {
            if (percent != get()) apply(percent);
            var text = percent == Min || percent == Max ? Loc.F($"{percent}%  (범위 {Min}% ~ {Max}%)") : $"{percent}%";
            if (report != null) report(text);
            else ShowBadge(target, text);
        }
    }

    // ── 비율을 잠깐 보여 주는 작은 표시 (창 가운데, 1초) ──
    private static System.Windows.Controls.Primitives.Popup? _badge;
    private static System.Windows.Threading.DispatcherTimer? _badgeTimer;

    private static void ShowBadge(UIElement target, string text)
    {
        if (_badge == null)
        {
            _badge = new System.Windows.Controls.Primitives.Popup
            {
                AllowsTransparency = true,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Center,
                IsHitTestVisible = false,
                Child = new System.Windows.Controls.Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xD8, 0x30, 0x30, 0x30)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(16, 8, 16, 8),
                    Child = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.White, FontSize = 18 },
                },
            };
            _badgeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            _badgeTimer.Tick += (_, _) => { _badgeTimer!.Stop(); _badge.IsOpen = false; };
        }
        ((System.Windows.Controls.TextBlock)((System.Windows.Controls.Border)_badge.Child).Child).Text = text;
        _badge.PlacementTarget = target;
        _badge.IsOpen = false;   // 다른 창에서 열려 있었으면 이 창으로 옮긴다
        _badge.IsOpen = true;
        _badgeTimer!.Stop();
        _badgeTimer.Start();
    }
}
