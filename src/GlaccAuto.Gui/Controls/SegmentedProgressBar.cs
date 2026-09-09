using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Utilities;
using Avalonia.VisualTree;

namespace GlaccAuto.Gui.Controls;

/// <summary>
/// Win11 设置（Windows 更新）风格的分段进度条：
/// 按段宽度比例分列，段间留缝，段内按已完成次数比例填充强调色；
/// 进度增长时填充以“从左向右生长”动画过渡（350ms，Win11 缓动）。
/// </summary>
public class SegmentedProgressBar : Grid
{
    public static readonly StyledProperty<IReadOnlyList<int>?> SegmentSizesProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, IReadOnlyList<int>?>(nameof(SegmentSizes));

    public static readonly StyledProperty<int> CompletedProperty =
        AvaloniaProperty.Register<SegmentedProgressBar, int>(nameof(Completed));

    public IReadOnlyList<int>? SegmentSizes
    {
        get => GetValue(SegmentSizesProperty);
        set => SetValue(SegmentSizesProperty, value);
    }

    public int Completed
    {
        get => GetValue(CompletedProperty);
        set => SetValue(CompletedProperty, value);
    }

    private List<Border> _fills = new();
    private bool _initialized;   // 首次绑定直接定格，不从零播放（页面切换重建视图时不重播）

    static SegmentedProgressBar()
    {
        SegmentSizesProperty.Changed.AddClassHandler<SegmentedProgressBar>((s, _) => s.Rebuild());
        CompletedProperty.Changed.AddClassHandler<SegmentedProgressBar>((s, e) =>
            s.OnCompletedChanged(e.GetOldValue<int>(), e.GetNewValue<int>()));
    }

    private void OnCompletedChanged(int oldValue, int newValue)
    {
        Rebuild();

        if (!_initialized)
        {
            _initialized = true;
            return;
        }

        // 增长时播放“生长”动画；回退（如重置演示）直接切换
        if (newValue > oldValue && oldValue >= 0)
        {
            AnimateGrowth(oldValue, newValue);
        }
    }

    private void Rebuild()
    {
        ColumnDefinitions.Clear();
        Children.Clear();
        _fills = new List<Border>();

        var sizes = SegmentSizes;
        if (sizes is null || sizes.Count == 0) return;

        IBrush trackBrush = Application.Current!.TryGetResource(
            "TrackBrush", ActualThemeVariant, out var tb) && tb is IBrush tBrush
                ? tBrush : (IBrush)Brushes.Gray;
        IBrush fillBrush = Application.Current.TryGetResource(
            "AccentBrush", ActualThemeVariant, out var ac) && ac is IBrush aBrush
                ? aBrush : (IBrush)Brushes.RoyalBlue;

        var total = sizes.Sum();
        var remaining = Math.Clamp(Completed, 0, total);
        var colIndex = 0;

        for (var i = 0; i < sizes.Count; i++)
        {
            if (i > 0)
            {
                // 段间 6px 缝隙
                ColumnDefinitions.Add(new ColumnDefinition(new GridLength(6)));
                colIndex++;
            }

            var size = sizes[i];
            var filled = Math.Min(remaining, size);
            remaining -= filled;

            var fillGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(filled, GridUnitType.Star)),
                    new ColumnDefinition(new GridLength(size - filled, GridUnitType.Star)),
                },
            };
            var fill = new Border
            {
                Background = fillBrush,
                // 部分完成时：左侧圆角、右侧直角贴合剩余轨道
                CornerRadius = filled == 0 ? default
                    : filled >= size ? new CornerRadius(3)
                    : new CornerRadius(3, 0, 0, 3),
            };
            Grid.SetColumn(fill, 0);
            fillGrid.Children.Add(fill);
            _fills.Add(fill);

            // 段容器：轨道底色 + 圆角裁剪
            var track = new Border
            {
                Height = 8,
                CornerRadius = new CornerRadius(3),
                Background = trackBrush,
                ClipToBounds = true,
                Child = fillGrid,
            };

            ColumnDefinitions.Add(new ColumnDefinition(new GridLength(size, GridUnitType.Star)));
            Grid.SetColumn(track, colIndex);
            Children.Add(track);
            colIndex++;
        }
    }

    /// <summary>对发生增长的段播放从旧比例到新比例的生长动画（ScaleX，原点左侧）。</summary>
    private void AnimateGrowth(int oldValue, int newValue)
    {
        var sizes = SegmentSizes;
        if (sizes is null) return;

        var start = 0;
        for (var i = 0; i < sizes.Count && i < _fills.Count; i++)
        {
            var size = sizes[i];
            var prev = Math.Clamp(oldValue - start, 0, size);
            var cur = Math.Clamp(newValue - start, 0, size);

            if (cur > prev)
            {
                var factor = size > 0 ? (double)prev / cur : 1.0;
                PlayGrow(_fills[i], factor);
            }

            start += size;
        }
    }

    private void PlayGrow(Border fill, double factor)
    {
        // 挂载前动画时钟不驱动：延迟到 AttachedToVisualTree 再播放
        if (!fill.IsAttachedToVisualTree())
        {
            void OnAttached(object? s, VisualTreeAttachmentEventArgs e)
            {
                fill.AttachedToVisualTree -= OnAttached;
                PlayGrow(fill, factor);
            }
            fill.AttachedToVisualTree += OnAttached;
            return;
        }

        var scale = new ScaleTransform { ScaleX = factor };
        fill.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
        fill.RenderTransform = scale;

        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(350),
            Easing = new SplineEasing(0.1, 0.9, 0.2, 1),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0.0),
                    Setters = { new Setter(ScaleTransform.ScaleXProperty, factor) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1.0),
                    Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0) },
                },
            },
        };

        // 动画目标必须是 Visual（TransformAnimator 要求），变换子属性通过 Setter 指定；
        // 结束后移除变换，避免 FillMode=None 回弹到局部值 ScaleX=factor
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        _ = anim.RunAsync(fill).ContinueWith(
            _ => fill.RenderTransform = null, ui);
    }
}
