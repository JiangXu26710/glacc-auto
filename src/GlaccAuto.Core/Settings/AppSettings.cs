using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlaccAuto.Core;

/// <summary>应用设置，持久化于 %APPDATA%\glacc-auto\settings.json</summary>
public sealed class AppSettings
{
    /// <summary>定时领取时刻（hh:mm）：计划任务不存在时作为回退值；任务存在时以任务的真实触发时间为准</summary>
    public string ScheduledTime { get; set; } = "08:00";
    public string ServerKey { get; set; } = "";
    public int IntervalMinSec { get; set; } = 30;
    public int IntervalMaxSec { get; set; } = 40;
    /// <summary>网络失败后的自动重试次数（0~10），0 = 失败立即中断；总尝试次数 = 1 + 此值</summary>
    public int NetworkRetryCount { get; set; } = 3;
    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";
    /// <summary>界面缩放百分比（75~250，步进 25），基准 125% = 当前 1.3x 设计</summary>
    public int ScalePercent { get; set; } = 125;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "glacc-auto", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(FilePath), AppSettingsJsonContext.Default.AppSettings);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));
        }
        catch
        {
            // 保存失败不阻断 UI
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;
