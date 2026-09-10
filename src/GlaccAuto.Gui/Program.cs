using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using GlaccAuto.Core.Diagnostics;

namespace GlaccAuto.Gui;

internal static class Program
{
    /// <summary>"到点领取"信号名：计划任务拉起第二个实例时转发给运行中实例</summary>
    internal const string ClaimSignalName = @"Local\glacc-auto-claim-signal";

    /// <summary>"唤出窗口"信号名：手动启动第二个实例时，请运行中实例把窗口恢复到前台</summary>
    internal const string ShowSignalName = @"Local\glacc-auto-show-signal";

    /// <summary>AllowSetForegroundWindow 的 ASFW_ANY：把前台权限授予任意进程</summary>
    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>致命错误提示框（MB_ICONERROR = 0x10）</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        DiagLog.Prune();
        DiagLog.Info($"启动 {AppInfo.VersionText}，{RuntimeInformation.FrameworkDescription}，{RuntimeInformation.OSDescription}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            DiagLog.Error("未处理异常，进程即将退出", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DiagLog.Error("后台任务未观察到的异常", e.Exception);
            e.SetObserved();
        };
        if (!OperatingSystem.IsWindows()) return 1;
        var scheduled = args.Any(a => a.Equals("--scheduled", StringComparison.OrdinalIgnoreCase));
        _singleInstance = new Mutex(true, @"Local\glacc-auto-single", out var isFirst);
        if (!isFirst)
        {
            if (scheduled)
            {
                // 应用已在运行：转发领取信号后立即退出，避免双实例同时推送
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ClaimSignalName);
                    signal.Set();
                }
                catch
                {
                    // 运行中实例刚退出等边界情况：放弃本次定时领取
                    DiagLog.Warn("到点领取：未找到运行中实例的领取信号，本次放弃");
                }
            }
            else
            {
                // 手动启动撞上运行中实例：先把前台权限让给运行中实例，再请它把窗口唤到前台；
                // 未获授权时该调用返回 false，运行中实例的恢复动作退化为任务栏闪烁。
                AllowSetForegroundWindow(AsfwAny);
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
                    signal.Set();
                }
                catch
                {
                    // 运行中实例尚未就绪：本次唤醒放弃
                    DiagLog.Warn("唤出窗口：未找到运行中实例的窗口信号，本次放弃");
                }
            }
            return 0;
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            DiagLog.Info("正常退出");
            return 0;
        }
        catch (Exception ex)
        {
            DiagLog.Error("应用异常退出", ex);
            FatalDialog(ex);
            return 2;
        }
    }

    /// <summary>致命错误提示：应用起不来时除日志外再弹系统提示框，告知日志文件位置。</summary>
    private static void FatalDialog(Exception ex)
    {
        try
        {
            MessageBoxW(IntPtr.Zero,
                $"glacc-auto 启动失败：{ex.Message}\n\n诊断日志：{DiagLog.CurrentFilePath}",
                "glacc-auto", 0x10);
        }
        catch
        {
            // 提示框不可用时仅保留日志
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
