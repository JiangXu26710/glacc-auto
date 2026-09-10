using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using GlaccAuto.Core;
using GlaccAuto.Gui.ViewModels;

namespace GlaccAuto.Gui;

public partial class MainWindow : Window
{
    // 基准窗口尺寸（DIP）：XAML 为官方 1.0x 规格，缩放因子 = 档位百分比 / 100
    private const double BaseWidth = 800, BaseHeight = 520;
    private const double BaseMinWidth = 704, BaseMinHeight = 480;
    private const int ScaleConfirmSeconds = 15;

    private readonly MainWindowViewModel _vm;
    private int _appliedScalePercent;   // 当前已应用的缩放（可能是倒计时中的待定值）
    private int _confirmedScalePercent; // 已确认缩放（还原目标）
    private int _pendingScalePercent;
    private int _countdown;
    private DispatcherTimer? _scaleTimer;

    public MainWindow(AppSettings settings, bool scheduledLaunch)
    {
        InitializeComponent();
        _vm = new MainWindowViewModel(settings, scheduledLaunch);
        DataContext = _vm;
        _vm.CloseRequested += Close;
        _vm.CopyRequested += text => _ = CopyToClipboardAsync(text);
        _vm.Settings.ScaleChangeRequested += OnScaleChangeRequested;

        // Win11 启用 Mica 材质背景；Win10 回退为主题实色背景（XAML 中的 DynamicResource）
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica };
            Background = Brushes.Transparent;
        }

        // 沉浸式标题栏：内容延伸到标题栏区域，保留系统窗口按钮
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = global::Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
        ExtendClientAreaTitleBarHeightHint = -1;

        TitleBar.PointerPressed += OnTitleBarPointerPressed;

        // 启动时按持久化缩放应用（不弹保护确认）
        _confirmedScalePercent = settings.ScalePercent;
        ApplyScale(settings.ScalePercent);

        StartShowSignalListener();

        // 计划任务拉起：窗口直接以最小化显示且不夺取焦点，避免打断用户当前操作；
        // 需要查看时点任务栏图标即可，领取结果另有 Server酱通知与窗口内提示。
        if (scheduledLaunch)
        {
            ShowActivated = false;
            WindowState = WindowState.Minimized;
        }
    }

    /// <summary>布局级缩放：窗口与内容同比（LayoutTransform 重排版，文字清晰）。</summary>
    private void ApplyScale(int percent)
    {
        _appliedScalePercent = percent;
        var f = percent / 100.0;
        ScaleHost.LayoutTransform = new ScaleTransform(f, f);
        Width = BaseWidth * f;
        Height = BaseHeight * f;
        MinWidth = BaseMinWidth * f;
        MinHeight = BaseMinHeight * f;
    }

    private void OnScaleChangeRequested(int percent)
    {
        if (percent == _appliedScalePercent) return;
        StopScaleTimer();
        _pendingScalePercent = percent;
        ApplyScale(percent);

        // 官方式保护：倒计时内未点"保留"则自动还原
        _countdown = ScaleConfirmSeconds;
        UpdateScaleCountdown();
        ScaleConfirmHost.IsVisible = true;
        _scaleTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            _countdown--;
            if (_countdown <= 0)
            {
                RevertScale();
            }
            else
            {
                UpdateScaleCountdown();
            }
        });
        _scaleTimer.Start();
    }

    private void UpdateScaleCountdown() =>
        ScaleCountdownText.Text = $"显示设置已更改，将在 {_countdown} 秒后恢复。";

    private void RevertScale()
    {
        StopScaleTimer();
        ScaleConfirmHost.IsVisible = false;
        _pendingScalePercent = _confirmedScalePercent;
        _vm.Settings.RevertScaleSilently(_confirmedScalePercent);
        ApplyScale(_confirmedScalePercent);
    }

    private void StopScaleTimer()
    {
        _scaleTimer?.Stop();
        _scaleTimer = null;
    }

    private void OnScaleKeepClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        StopScaleTimer();
        _confirmedScalePercent = _pendingScalePercent;
        ScaleConfirmHost.IsVisible = false;
    }

    private void OnScaleRevertClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        RevertScale();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            BeginMoveDrag(e);
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm.CancelExitCommand.Execute(null);
    }

    /// <summary>
    /// 点击登录对话框遮罩：取消登录。验证码步骤忽略遮罩点击，
    /// 避免误触把已发出的验证码作废、需重新发送。
    /// </summary>
    private void OnLoginScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm.LoginCodeStep) return;
        _vm.CancelLoginCommand.Execute(null);
    }

    /// <summary>把诊断信息写入系统剪贴板；剪贴板被占用等失败情形静默忽略（界面已给反馈）。</summary>
    private async Task CopyToClipboardAsync(string text)
    {
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch
        {
            // 忽略：不影响领取流程
        }
    }

    /// <summary>
    /// 监听"唤出窗口"信号：手动启动了第二个实例（单实例接管）时，
    /// 把窗口从最小化恢复到前台。计划任务拉起的实例不主动打扰用户，只响应用户的手动启动。
    /// </summary>
    private void StartShowSignalListener()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSignalName);
                while (signal.WaitOne())
                {
                    await Dispatcher.UIThread.InvokeAsync(BringToFront);
                }
            }
            catch
            {
                // 信号监听不可用仅损失"手动启动时唤出窗口"能力，不影响领取流程
            }
        });
    }

    /// <summary>恢复最小化的窗口并置于前台。</summary>
    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 领取中关闭窗口：拦截并弹二次确认；窗口最小化时先恢复，否则确认层在任务栏里看不见
        if (!_vm.ExitConfirmed && _vm.State == RunState.Running)
        {
            e.Cancel = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            _vm.ShowExitConfirm = true;
        }
        StopScaleTimer();
        base.OnClosing(e);
    }
}
