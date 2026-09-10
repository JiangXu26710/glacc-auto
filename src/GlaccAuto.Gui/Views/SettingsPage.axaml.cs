using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace GlaccAuto.Gui.Views;

public partial class SettingsPage : UserControl
{
    private CancellationTokenSource? _revealCts;

    /// <summary>子卡片总高：分隔线(1) + 两条子行(48×2) + 分隔线(1)</summary>
    private const double SubBlockHeight = 98;

    public SettingsPage()
    {
        InitializeComponent();
        Focusable = true; // 可承接"点空白释放焦点"的转移焦点

        // 点击卡片/页面空白处时释放输入框焦点（Avalonia 默认点击不可聚焦区域不会清除焦点）
        this.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is Visual source &&
                source.GetSelfAndVisualAncestors().OfType<TextBox>().Any())
            {
                return; // 点在输入框内部：保留焦点
            }
            this.Focus();
        });
    }

    // ServerKey：聚焦时明文，失焦显示圆点占位符
    private void ServerKey_OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        ServerKeyBox.PasswordChar = default;
    }

    private void ServerKey_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        ServerKeyBox.PasswordChar = '●';
    }

    // 领取间隔：失焦时以输入框文本为准做夹紧校验，并把合法值直接写回控件
    // （失焦触发的绑定写源过程中，VM 侧的属性变更回写会被绑定引擎抑制，文本无法经绑定自动纠正）
    private void IntervalBox_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && DataContext is ViewModels.SettingsViewModel vm)
        {
            var coerced = vm.CoerceIntervalInput(box.Text, isMin: box == IntervalMinBox);
            if (box.Text != coerced)
            {
                box.Text = coerced;
            }
        }
    }

    // "定时领取"行：点击展开/收起子行；点在开关上时不切换展开状态
    private void ScheduleRow_OnTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ViewModels.SettingsViewModel vm)
        {
            return;
        }
        if (e.Source is Visual source &&
            source.GetSelfAndVisualAncestors().OfType<ToggleSwitch>().Any())
        {
            return; // 开关自己处理开/关
        }
        vm.ScheduleExpanded = !vm.ScheduleExpanded;
        // 箭头：直接换字形（E70D ∨ / E70E ∧），位置由固定 32px 列保证，无旋转带来的微小偏移
        ScheduleChevron.Text = vm.ScheduleExpanded ? "\uE70E" : "\uE70D";
        ToggleClass(ScheduleSub, "expanded", vm.ScheduleExpanded);
        ToggleClass(ScheduleSubCard, "expanded", vm.ScheduleExpanded);
        // 展开时父卡与子卡连为一体：父卡去底边框/底圆角，子卡去顶边框/顶圆角（相接处仅剩分隔线）
        ScheduleHeaderCard.CornerRadius = vm.ScheduleExpanded
            ? new Avalonia.CornerRadius(8, 8, 0, 0) : new Avalonia.CornerRadius(8);
        ScheduleHeaderCard.BorderThickness = vm.ScheduleExpanded
            ? new Avalonia.Thickness(1, 1, 1, 0) : new Avalonia.Thickness(1);
        ScheduleSubCard.CornerRadius = vm.ScheduleExpanded
            ? new Avalonia.CornerRadius(0, 0, 8, 8) : new Avalonia.CornerRadius(8);
        ScheduleSubCard.BorderThickness = vm.ScheduleExpanded
            ? new Avalonia.Thickness(1, 0, 1, 1) : new Avalonia.Thickness(1);
        // 官方式两段式揭示动画：显式 Animation 驱动（样式 Transitions 在真实点击下不触发，弃用）
        RunRevealAnimation(vm.ScheduleExpanded);
    }

    private static void ToggleClass(Avalonia.Controls.Control c, string className, bool on)
    {
        if (on) c.Classes.Add(className);
        else c.Classes.Remove(className);
    }

    /// <summary>Win11 官方两段式揭示：避让与浮出完全同步（同 300ms 同起同止）。
    /// 收起：子卡片滑回父卡后方与空间合拢同时进行（同 250ms）。</summary>
    private async void RunRevealAnimation(bool expanding)
    {
        _revealCts?.Cancel();
        _revealCts = new CancellationTokenSource();
        var ct = _revealCts.Token;
        // 从当前值起步，快速连点时不跳变
        var h = ScheduleSub.Height;
        if (double.IsNaN(h)) h = 0;
        var op = ScheduleSubCard.Opacity;
        var y = ((Avalonia.Media.TranslateTransform?)ScheduleSubCard.RenderTransform)?.Y ?? -10;
        try
        {
            if (expanding)
            {
                ScheduleSub.IsVisible = true;
                // 避让与浮出完全同步：空间撑开的同时子内容淡入+下移（同时开始、同时结束）
                var avoidTask = MakeAnim(Avalonia.Controls.Panel.HeightProperty, h, SubBlockHeight, 300, decelerate: true)
                    .RunAsync(ScheduleSub, ct);
                var t1 = MakeAnim(Avalonia.Visual.OpacityProperty, op, 1d, 300, decelerate: true).RunAsync(ScheduleSubCard, ct);
                var t2 = MakeAnim(Avalonia.Media.TranslateTransform.YProperty, y, 0d, 300, decelerate: true).RunAsync(ScheduleSubCard, ct);
                await Task.WhenAll(avoidTask, t1, t2);
            }
            else
            {
                // 收起（官方擦除式）：子内容原地不动（无滑动），仅面板高度匀速合拢——
                // 底部裁切线上移，输入框与背景以同一速度被裁掉，无相对运动。
                // 两个 1ms 动画：归零 Opacity 保持值 + 复位 Y=-10（卡片此刻已被隐藏，瞬移无观感）
                _ = MakeAnim(Avalonia.Visual.OpacityProperty, op, 0d, 1, decelerate: false).RunAsync(ScheduleSubCard, ct);
                _ = MakeAnim(Avalonia.Media.TranslateTransform.YProperty, y, -10d, 1, decelerate: true).RunAsync(ScheduleSubCard, ct);
                await MakeAnim(Avalonia.Controls.Panel.HeightProperty, h, 0d, 250, decelerate: false)
                    .RunAsync(ScheduleSub, ct);
                ScheduleSub.IsVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
            // 快速连点时上一次动画被取消，属正常路径
        }
    }

    private static Avalonia.Animation.Animation MakeAnim(
        AvaloniaProperty property, double from, double to, int ms, bool decelerate)
    {
        return new Avalonia.Animation.Animation
        {
            Duration = TimeSpan.FromMilliseconds(ms),
            FillMode = Avalonia.Animation.FillMode.Forward,
            Easing = decelerate
                ? new Avalonia.Animation.Easings.SplineEasing(0, 0, 0, 1)
                : new Avalonia.Animation.Easings.LinearEasing(),
            Children =
            {
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(0.0),
                    Setters = { new Avalonia.Styling.Setter(property, from) }
                },
                new Avalonia.Animation.KeyFrame
                {
                    Cue = new Avalonia.Animation.Cue(1.0),
                    Setters = { new Avalonia.Styling.Setter(property, to) }
                },
            }
        };
    }
}
