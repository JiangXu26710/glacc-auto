using System.Text;

namespace GlaccAuto.Core.Diagnostics;

/// <summary>
/// 本机诊断日志：按天写入 %APPDATA%\glacc-auto\logs\，用于记录运行状态与技术细节。
/// 写入失败一律静默（磁盘/权限问题不能影响业务），不记录任何凭证。
/// </summary>
public static class DiagLog
{
    /// <summary>日志保留天数，超期文件在启动时清理</summary>
    private const int RetentionDays = 7;

    private static readonly object Gate = new();

    /// <summary>日志目录</summary>
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "glacc-auto", "logs");

    /// <summary>当前日志文件路径（一天一个文件）</summary>
    public static string CurrentFilePath => Path.Combine(DirectoryPath, $"glacc-auto-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    /// <summary>追加一行日志；任何写入失败都静默忽略。</summary>
    public static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (ex is not null) line += Environment.NewLine + ex;
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(CurrentFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志不可用时静默：业务流程优先
        }
    }

    /// <summary>清理过期日志（仅本应用日志目录内、文件名匹配前缀的文件）。</summary>
    public static void Prune()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return;
            var deadline = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "glacc-auto-*.log"))
            {
                if (File.GetLastWriteTime(file) < deadline) File.Delete(file);
            }
        }
        catch
        {
            // 清理失败不影响本次日志写入
        }
    }
}
