using System.Threading;
using Avalonia;

namespace GlaccAuto.Gui;

internal static class Program
{
    /// <summary>"到点领取"信号名：计划任务拉起第二个实例时转发给运行中实例</summary>
    internal const string ClaimSignalName = @"Local\glacc-auto-claim-signal";

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
