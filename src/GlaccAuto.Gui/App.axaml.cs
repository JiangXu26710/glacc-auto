using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using GlaccAuto.Core;
using GlaccAuto.Core.Diagnostics;
using GlaccAuto.Gui.ViewModels;

namespace GlaccAuto.Gui;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 界面线程异常：先落日志，再按原有语义继续抛出（不吞异常，避免状态不一致）
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            DiagLog.Error("界面线程未处理异常", e.Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // FluentAvalonia 默认跟随系统强调色；统一为应用令牌 #0078D4 保持一致
            Current?.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault()
                ?.CustomAccentColor = Avalonia.Media.Color.FromRgb(0x00, 0x78, 0xD4);

            var settings = AppSettings.Load();
            ApplyTheme(settings.Theme);
            // 计划任务以 --scheduled 参数拉起：恢复登录态后自动执行领取
            var scheduledLaunch = desktop.Args?.Any(
                a => a.Equals("--scheduled", StringComparison.OrdinalIgnoreCase)) == true;
            desktop.MainWindow = new MainWindow(settings, scheduledLaunch);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyTheme(string? theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => null, // 跟随系统
        };

        // 切换主题会丢失 Mica backdrop，需重新应用（Win11 且主窗口已创建时）
        if (Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is Window win &&
            Environment.OSVersion.Version.Build >= 22000)
        {
            win.TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica };
            win.Background = Avalonia.Media.Brushes.Transparent;
        }
    }
}
