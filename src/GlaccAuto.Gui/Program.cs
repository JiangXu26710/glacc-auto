using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;

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

    private static Mutex? _singleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
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
                }
            }
            return 0;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
