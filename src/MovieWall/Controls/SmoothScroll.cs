using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MovieWall.Controls;

/// <summary>
/// 平滑滚轮。
///
/// WPF 默认的滚轮是「一格跳三行」的硬切，海报墙滚起来像幻灯片。
/// 这里接管 PreviewMouseWheel，用 CompositionTarget.Rendering 每帧做一次指数插值，
/// 让滚动带惯性收尾——这是让它「摸起来像原生」的关键一环。
///
/// 用法：&lt;ScrollViewer controls:SmoothScroll.IsEnabled="True"&gt;
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScroll),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    /// <summary>每个滚轮刻度滚动的像素数。</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(double), typeof(SmoothScroll), new PropertyMetadata(180.0));

    public static void SetStep(DependencyObject element, double value) => element.SetValue(StepProperty, value);
    public static double GetStep(DependencyObject element) => (double)element.GetValue(StepProperty);

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    private static int _diagBudget = 8;

    private sealed class State
    {
        public double Target;
        public double Current;
        public bool Animating;
        public EventHandler? Tick;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;

        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            sv.ScrollChanged += OnScrollChanged;
            sv.Unloaded += OnUnloaded;
        }
        else
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.ScrollChanged -= OnScrollChanged;
            sv.Unloaded -= OnUnloaded;
            StopAnimation(sv);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => StopAnimation((ScrollViewer)sender);

    private static State GetState(ScrollViewer sv)
    {
        if (States.TryGetValue(sv, out var s)) return s;
        s = new State { Current = sv.VerticalOffset, Target = sv.VerticalOffset };
        States.Add(sv, s);
        return s;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;

        // Ctrl+滚轮 交给外部（缩放海报尺寸之类），不劫持
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            sv.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sv
            });
            return;
        }

        // 用 ExtentHeight - ViewportHeight 而不是 ScrollableHeight：
        // 面板实现 IScrollInfo 时，ScrollViewer 的 ScrollableHeight 会慢一拍同步，
        // 刚布局完就滚轮的话读到的可能是 0，于是整下滚被判定成「没内容可滚」直接吞掉。
        double maxOffset = Math.Max(0, sv.ExtentHeight - sv.ViewportHeight);

        if (_diagBudget > 0 && Interlocked.Decrement(ref _diagBudget) > 0)
            Services.Log.Write($"wheel: delta={e.Delta} ext={sv.ExtentHeight:0} vp={sv.ViewportHeight:0} " +
                               $"scrollable={sv.ScrollableHeight:0} max={maxOffset:0} off={sv.VerticalOffset:0}");

        if (maxOffset <= 0.5) return;

        e.Handled = true;
        var st = GetState(sv);
        var step = GetStep(sv) * (e.Delta / 120.0);

        // 目标点累积，快速连滚时不会「吃掉」中间量
        st.Target = Math.Max(0, Math.Min(st.Target - step, maxOffset));
        if (!st.Animating) StartAnimation(sv, st);
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (Math.Abs(e.VerticalChange) < 0.01 && Math.Abs(e.ExtentHeightChange) < 0.01
            && Math.Abs(e.ViewportHeightChange) < 0.01) return;

        var st = GetState(sv);

        // 动画过程中由我们自己写 offset，不同步
        if (st.Animating) return;

        // 用户直接拖滚动条 / 键盘翻页 / 内容尺寸变化——把缓动目标拉回真实位置
        double maxOffset = Math.Max(0, sv.ExtentHeight - sv.ViewportHeight);
        st.Current = sv.VerticalOffset;
        st.Target = Math.Max(0, Math.Min(st.Target, maxOffset));
        if (Math.Abs(st.Target - st.Current) > maxOffset * 0.5) st.Target = st.Current;
    }

    private static void StartAnimation(ScrollViewer sv, State st)
    {
        if (st.Tick is not null) return;

        st.Animating = true;
        st.Current = sv.VerticalOffset;

        st.Tick = (_, _) =>
        {
            double diff = st.Target - st.Current;

            if (Math.Abs(diff) < 0.75)
            {
                st.Current = st.Target;
                sv.ScrollToVerticalOffset(st.Current);
                StopAnimation(sv);
                return;
            }

            // 0.28 是手感分水岭：太小拖泥带水，太大就变成硬切了
            st.Current += diff * 0.28;
            sv.ScrollToVerticalOffset(st.Current);
        };

        CompositionTarget.Rendering += st.Tick;
    }

    private static void StopAnimation(ScrollViewer sv)
    {
        if (!States.TryGetValue(sv, out var st)) return;
        if (st.Tick is not null)
        {
            CompositionTarget.Rendering -= st.Tick;
            st.Tick = null;
        }
        st.Animating = false;
        st.Current = sv.VerticalOffset;
    }
}
