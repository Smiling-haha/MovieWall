using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MovieWall.Models;

namespace MovieWall.Controls;

/// <summary>
/// 一张海报卡。参考设计里卡片信息在图片下方（而不是悬停浮层），
/// 所以悬停反馈做得很克制：微微上浮 + 一点点提亮 + 一道极淡的投影。
/// </summary>
public partial class PosterCard : UserControl
{
    private static int _introBudget = 90;

    private DropShadowEffect? _shadow;
    private System.ComponentModel.DependencyPropertyDescriptor? _hoverDpd;

    public event EventHandler<Movie>? CardClicked;

    public PosterCard()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateClip();
        MouseLeftButtonUp += OnMouseUp;
    }

    private Movie? Model => DataContext as Movie;

    // ---------------- 生命周期 ----------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateClip();

        if (_hoverDpd is null)
        {
            _hoverDpd = System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(IsMouseOverProperty, typeof(PosterCard));
            _hoverDpd.AddValueChanged(this, OnHoverChanged);
        }

        ResetVisualState();
        if (IsMouseOver) HoverIn();

        BeginIntro();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hoverDpd is not null)
        {
            _hoverDpd.RemoveValueChanged(this, OnHoverChanged);
            _hoverDpd = null;
        }

        _shadow?.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        PosterHost.Effect = null;
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void OnHoverChanged(object? sender, EventArgs e)
    {
        if (IsMouseOver) HoverIn();
        else HoverOut();
    }

    private void ResetVisualState()
    {
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;

        CardShift.BeginAnimation(TranslateTransform.YProperty, null);
        CardShift.Y = 0;

        HoverVeil.BeginAnimation(OpacityProperty, null);
        HoverVeil.Opacity = 0;

        PosterHost.Effect = null;
    }

    private void UpdateClip()
    {
        var w = Poster.ActualWidth;
        var h = Poster.ActualHeight;
        if (w <= 0 || h <= 0) return;

        if (Poster.Clip is RectangleGeometry g
            && Math.Abs(g.Rect.Width - w) < 0.5
            && Math.Abs(g.Rect.Height - h) < 0.5)
            return;

        // Border 的圆角不会裁剪子元素，海报的圆角必须自己 Clip
        Poster.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 5, 5);
    }

    // ---------------- 入场 ----------------

    private void BeginIntro()
    {
        if (Interlocked.Decrement(ref _introBudget) < 0) return;

        var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 110));

        Host.Opacity = 0;
        CardShift.Y = 12;

        Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340))
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0,
            TimeSpan.FromMilliseconds(420))
        {
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // ---------------- 悬停 ----------------

    private void HoverIn()
    {
        Animate(CardScale, ScaleTransform.ScaleXProperty, 1.018, 0.22);
        Animate(CardScale, ScaleTransform.ScaleYProperty, 1.018, 0.22);
        Animate(CardShift, TranslateTransform.YProperty, -3, 0.24);
        Animate(HoverVeil, OpacityProperty, 1, 0.22);
        ShowShadow();
    }

    private void HoverOut()
    {
        Animate(CardScale, ScaleTransform.ScaleXProperty, 1.0, 0.20);
        Animate(CardScale, ScaleTransform.ScaleYProperty, 1.0, 0.20);
        Animate(CardShift, TranslateTransform.YProperty, 0, 0.22);
        Animate(HoverVeil, OpacityProperty, 0, 0.18);
        HideShadow();
    }

    private void ShowShadow()
    {
        _shadow ??= new DropShadowEffect
        {
            BlurRadius = 26,
            ShadowDepth = 8,
            Direction = 270,
            Color = Colors.Black,
            Opacity = 0,
            RenderingBias = RenderingBias.Performance
        };

        PosterHost.Effect = _shadow;
        Animate(_shadow, DropShadowEffect.OpacityProperty, 0.7, 0.24);
    }

    private void HideShadow()
    {
        if (_shadow is null) return;

        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.2))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) => PosterHost.Effect = null;
        _shadow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
    }

    // ---------------- 点击 ----------------

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { } movie) return;
        if (e.ChangedButton != MouseButton.Left) return;

        e.Handled = true;
        CardClicked?.Invoke(this, movie);
    }

    // ---------------- 辅助 ----------------

    private static void Animate(IAnimatable target, DependencyProperty prop, double to,
        double seconds, double delaySeconds = 0)
    {
        target.BeginAnimation(prop, new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromSeconds(seconds),
            BeginTime = delaySeconds > 0 ? TimeSpan.FromSeconds(delaySeconds) : null,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        });
    }
}
