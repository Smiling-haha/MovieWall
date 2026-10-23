using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MovieWall.Controls;

/// <summary>
/// 虚拟化的等高海报网格。
///
/// WPF 自带的 WrapPanel 不虚拟化，一千部电影就是一千个 Border + 一千个 Image，
/// 滚动必卡、内存必爆。这个 Panel 只生成「可视区 + 1 行缓冲」里的容器，
/// 其余全部回收，所以库里有多少片子和滚动流畅度基本无关。
///
/// === 这里为什么不实现 IScrollInfo ===
/// 早期版本实现了它，结果整面墙根本滚不动：ScrollViewer 的 extent 被算成了视口高度。
/// 原因很隐蔽——ScrollContentPresenter 的直接 child 是 ItemsPresenter，不是本 Panel，
/// 所以「把父级 ScrollContentPresenter 记到 IScrollInfo.ScrollOwner 上」这条链断掉了，
/// ScrollViewer 压根不知道面板报的 extent，只能拿 DesiredSize 当内容高度，
/// 而 DesiredSize 又正好等于视口高度 → 可滚距离 0，滚轮全被吞掉。
///
/// 现在的做法是纯像素滚动：
///   · MeasureOverride 返回内容真实总高度 → ScrollViewer 正常可滚
///   · ScrollContentPresenter 自己平移内容 → 不需要我们减 offset
///   · 本 Panel 只负责「按当前滚动位置决定渲染哪几行」，跨越行边界时才重排
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel
{
    // ---------------- 依赖属性 ----------------

    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(360.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty HorizontalPaddingProperty = DependencyProperty.Register(
        nameof(HorizontalPadding), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(48.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty VerticalPaddingProperty = DependencyProperty.Register(
        nameof(VerticalPadding), typeof(double), typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ItemWidth { get => (double)GetValue(ItemWidthProperty); set => SetValue(ItemWidthProperty, value); }
    public double ItemHeight { get => (double)GetValue(ItemHeightProperty); set => SetValue(ItemHeightProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double HorizontalPadding { get => (double)GetValue(HorizontalPaddingProperty); set => SetValue(HorizontalPaddingProperty, value); }
    public double VerticalPadding { get => (double)GetValue(VerticalPaddingProperty); set => SetValue(VerticalPaddingProperty, value); }

    // ---------------- 内部状态 ----------------

    private double _offsetY;
    private double _viewportH;
    private int _columns = 1;
    private double _rowStride = 1;
    private int _itemCount;
    private int _lastFirstRow = -1;
    private int _lastLastRow = -1;

    private ScrollViewer? _sv;
    private ScrollContentPresenter? _presenter;

    // ---------------- 布局 ----------------

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        _itemCount = owner?.Items.Count ?? 0;

        SyncSubscriptions();

        var (viewportWidth, viewportHeight) = ResolveViewport(availableSize);
        _viewportH = viewportHeight;

        double usable = viewportWidth - HorizontalPadding * 2;
        _columns = Math.Max(1, (int)Math.Floor((usable + Spacing) / (ItemWidth + Spacing)));
        _rowStride = ItemHeight + Spacing;

        int rows = _itemCount == 0 ? 0 : (int)Math.Ceiling(_itemCount / (double)_columns);
        double extentHeight = rows == 0 ? 0 : rows * _rowStride - Spacing + VerticalPadding * 2;

        if (_itemCount == 0)
        {
            CleanUpItems(0, -1);
            _lastFirstRow = _lastLastRow = -1;
            return new Size(viewportWidth, viewportHeight);
        }

        // 可视行范围（多算 1 行作缓冲，滚动时不会看到空白）
        int firstRow = Math.Max(0, (int)Math.Floor((_offsetY - VerticalPadding) / _rowStride));
        int lastRow = (int)Math.Ceiling((_offsetY - VerticalPadding + viewportHeight) / _rowStride);

        int firstIndex = firstRow * _columns;
        int lastIndex = Math.Min(_itemCount - 1, (lastRow + 1) * _columns - 1);

        CleanUpItems(firstIndex, lastIndex);
        if (firstIndex <= lastIndex) GenerateItems(firstIndex, lastIndex);

        _lastFirstRow = firstRow;
        _lastLastRow = lastRow;

        if (_diagBudget > 0 && Interlocked.Decrement(ref _diagBudget) > 0)
            Services.Log.Write($"panel: n={_itemCount} cols={_columns} ext={extentHeight:0} " +
                               $"vp={viewportWidth:0}x{viewportHeight:0} off={_offsetY:0} rows={firstRow}..{lastRow}");

        // 返回真实内容高度，ScrollViewer 才知道能滚多远
        return new Size(viewportWidth, Math.Max(extentHeight, viewportHeight));
    }

    private static int _diagBudget = 24;

    private void GenerateItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return;

        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = startPos.Offset == 0 ? startPos.Index : startPos.Index + 1;

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (int itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                if (generator.GenerateNext(out bool isNew) is not UIElement child) break;

                if (isNew)
                {
                    if (childIndex >= InternalChildren.Count)
                        AddInternalChild(child);
                    else
                        InsertInternalChild(childIndex, child);

                    generator.PrepareItemContainer(child);
                }

                child.Measure(new Size(ItemWidth, ItemHeight));
            }
        }
    }

    private void CleanUpItems(int minIndex, int maxIndex)
    {
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        if (generator is null) return;

        for (int i = children.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            int itemIndex = generator.IndexFromGeneratorPosition(pos);

            if (itemIndex < minIndex || itemIndex > maxIndex || itemIndex < 0)
            {
                generator.Remove(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return finalSize;

        // 绝对坐标：内容的平移由 ScrollContentPresenter 负责，这里不要再减 offset
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            int itemIndex = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (itemIndex < 0) continue;

            int row = itemIndex / _columns;
            int col = itemIndex % _columns;

            double x = HorizontalPadding + col * (ItemWidth + Spacing);
            double y = VerticalPadding + row * _rowStride;

            child.Arrange(new Rect(x, y, ItemWidth, ItemHeight));
        }

        return finalSize;
    }

    // ---------------- 滚动联动 ----------------

    private void SyncSubscriptions()
    {
        var sv = FindAncestor<ScrollViewer>();
        if (!ReferenceEquals(sv, _sv))
        {
            if (_sv is not null) _sv.ScrollChanged -= OnScrollChanged;
            _sv = sv;
            if (_sv is not null)
            {
                _sv.ScrollChanged += OnScrollChanged;
                _offsetY = _sv.VerticalOffset;
            }
        }

        _presenter = FindAncestor<ScrollContentPresenter>();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _offsetY = _sv?.VerticalOffset ?? 0;

        // 只有「可视行范围」真的变了才重排。
        // 否则每帧 InvalidateMeasure 会把滚动拖成幻灯片——虽然内容位置是固定坐标，
        // 但重新测量会重建容器，代价不小。
        int firstRow = Math.Max(0, (int)Math.Floor((_offsetY - VerticalPadding) / _rowStride));
        int lastRow = (int)Math.Ceiling((_offsetY - VerticalPadding + _viewportH) / _rowStride);

        if (firstRow == _lastFirstRow && lastRow == _lastLastRow) return;

        InvalidateMeasure();
    }

    private T? FindAncestor<T>() where T : DependencyObject
    {
        DependencyObject? cur = VisualTreeHelper.GetParent(this);
        while (cur is not null)
        {
            if (cur is T t) return t;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    /// <summary>
    /// 拿到真正的视口尺寸。
    /// CanContentScroll 关掉后，ScrollViewer 传给面板的 availableSize 高度是正无穷
    /// （宽度是真的），所以高度必须从 ScrollContentPresenter 的实际布局尺寸里取。
    /// </summary>
    private (double width, double height) ResolveViewport(Size availableSize)
    {
        double w = 0, h = 0;

        if (_presenter is not null)
        {
            w = _presenter.ActualWidth;
            h = _presenter.ActualHeight;
        }

        if (w <= 0 || h <= 0)
        {
            if (_sv is { ActualWidth: > 0 })
            {
                w = _sv.ActualWidth;
                h = _sv.ActualHeight;
            }
        }

        if (w <= 0) w = double.IsInfinity(availableSize.Width) ? 960 : availableSize.Width;
        if (h <= 0) h = double.IsInfinity(availableSize.Height) ? 700 : availableSize.Height;

        // 竖直滚动条占位，避免「出现滚动条→列数变少→不再需要滚动条→列数变回来」的抖动
        w -= SystemParameters.VerticalScrollBarWidth;

        return (Math.Max(ItemWidth + HorizontalPadding * 2, w), Math.Max(ItemHeight, h));
    }

    // ---------------- Items 变化 ----------------

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                try { RemoveInternalChildRange(args.Position.Index, args.ItemUICount); }
                catch (ArgumentOutOfRangeException) { }
                break;
            case NotifyCollectionChangedAction.Reset:
                // Reset 时 WPF 会自行清空，这里只需让偏移量合法
                break;
        }
        InvalidateMeasure();
    }

    /// <summary>滚动时让面板把新进入视野的项生成出来。</summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateMeasure();
    }
}
