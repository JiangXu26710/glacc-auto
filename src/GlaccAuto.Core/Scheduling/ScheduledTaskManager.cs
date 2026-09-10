using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace GlaccAuto.Core.Scheduling;

/// <summary>
/// Windows 计划任务管理：注册/注销/校验每日定时领取任务（当前用户级，无需管理员）。
/// 任务到点以 --scheduled 参数拉起本应用，由 GUI 自动执行领取流程。
/// </summary>
public static class ScheduledTaskManager
{
    public const string TaskName = "glacc-auto-claim";
    public const string LaunchArgument = "--scheduled";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>注册/注销失败时抛出，消息可直接展示给用户。</summary>
    public sealed class ScheduledTaskException(string message) : Exception(message);

    /// <summary>任务定义快照，取自 schtasks /Query 的 XML 导出。</summary>
    public sealed record TaskSnapshot(
        bool Enabled,
        bool TriggerEnabled,
        bool Daily,
        TimeSpan StartTime,
        string Command,
        string Arguments,
        bool StartWhenAvailable,
        bool DisallowStartIfOnBatteries,
        bool StopIfGoingOnBatteries);

    /// <summary>
    /// 快照是否与预期一致：每日 time 时刻、当前 exe + --scheduled、任务与触发器均启用。
    /// </summary>
    public static bool MatchesExpectation(TaskSnapshot? snapshot, TimeSpan time)
    {
        if (snapshot is null) return false;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        return snapshot.Enabled
            && snapshot.TriggerEnabled
            && snapshot.Daily
            && snapshot.StartTime.Hours == time.Hours
            && snapshot.StartTime.Minutes == time.Minutes
            && string.Equals(snapshot.Command.Trim('"'), exe, StringComparison.OrdinalIgnoreCase)
            && snapshot.Arguments.Trim() == LaunchArgument
            && !snapshot.StartWhenAvailable
            && !snapshot.DisallowStartIfOnBatteries
            && !snapshot.StopIfGoingOnBatteries;
    }

    /// <summary>
    /// 查询任务定义。返回 true = 查询成功（此时 snapshot 为 null 表示任务不存在）；
    /// 返回 false = 查询本身失败（schtasks 异常、超时或输出无法解析），此时 snapshot 恒为 null。
    /// </summary>
    public static bool TryQuery(out TaskSnapshot? snapshot)
    {
        snapshot = null;
        string stdout;
        try
        {
            var (code, output, _) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
            // schtasks 对不存在的任务返回非零退出码：查询本身成功，结论是任务不存在
            if (code != 0) return true;
            stdout = output;
        }
        catch
        {
            // 调用超时等执行层失败：无法判断任务状态
            return false;
        }
        try
        {
            var root = XDocument.Parse(stdout).Root;
            var settings = root?.Element(Ns + "Settings");
            var trigger = root?.Element(Ns + "Triggers")?.Element(Ns + "CalendarTrigger");
            var exec = root?.Element(Ns + "Actions")?.Element(Ns + "Exec");
            var start = trigger?.Element(Ns + "StartBoundary")?.Value;
            if (settings is null || trigger is null || exec is null || start is null) return false;
            if (!DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out var st))
                return false;
            var daily = int.TryParse(
                trigger.Element(Ns + "ScheduleByDay")?.Element(Ns + "DaysInterval")?.Value,
                out var days) && days == 1;
            snapshot = new TaskSnapshot(
                // 导出的 XML 中 Enabled 在启用时缺省、禁用时才写 false
                Enabled: ElementText(settings, "Enabled") != "false",
                TriggerEnabled: ElementText(trigger, "Enabled") != "false",
                Daily: daily,
                StartTime: st.TimeOfDay,
                Command: ElementText(exec, "Command"),
                Arguments: ElementText(exec, "Arguments"),
                StartWhenAvailable: ElementText(settings, "StartWhenAvailable") == "true",
                // 电池两项导出缺省时按 Windows 默认 true（不允许电池启动）处理，会触发重注册纠正
                DisallowStartIfOnBatteries: ElementText(settings, "DisallowStartIfOnBatteries") != "false",
                StopIfGoingOnBatteries: ElementText(settings, "StopIfGoingOnBatteries") != "false");
            return true;
        }
        catch
        {
            // 输出无法解析：无法判断任务状态
            return false;
        }
    }

    /// <summary>按当前 exe 与指定时刻注册任务（/F 幂等覆盖）。</summary>
    public static void Register(TimeSpan time)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            throw new ScheduledTaskException("无法确定应用可执行文件路径");
        // 以 XML 导入而非命令行 /TR 注册：避开路径含空格的转义，且可显式关闭"仅交流电可启动"限制
        var tmp = Path.Combine(Path.GetTempPath(), "glacc-auto-task.xml");
        File.WriteAllText(tmp, BuildTaskXml(exe, time), Encoding.Unicode);
        try
        {
            var (code, _, stderr) = RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{tmp}\"");
            if (code != 0)
                throw new ScheduledTaskException(Describe(stderr));
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* 临时文件清理失败不影响注册结果 */ }
        }
    }

    /// <summary>注销任务；任务不存在视为已注销。</summary>
    public static void Unregister()
    {
        // 无条件先删除：查询失败（瞬时故障）不应把仍在的任务误判为已注销而跳过删除
        var (code, _, stderr) = RunSchtasks($"/Delete /F /TN \"{TaskName}\"");
        if (code == 0) return;
        // 删除失败后查询复核：查询成功且确认任务不存在（含外部已删/竞态删除）才视为幂等成功；
        // 查询本身失败时如实报错，避免把无法确认的状态当成已注销
        if (TryQuery(out var snapshot) && snapshot is null) return;
        throw new ScheduledTaskException(Describe(stderr));
    }

    private static string BuildTaskXml(string exe, TimeSpan time)
    {
        var start = $"{DateTime.Today:yyyy-MM-dd}T{time.Hours:00}:{time.Minutes:00}:00";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="{Ns}">
              <RegistrationInfo>
                <Description>glacc-auto 每日定时领取</Description>
              </RegistrationInfo>
              <Triggers>
                <CalendarTrigger>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                  <ScheduleByDay>
                    <DaysInterval>1</DaysInterval>
                  </ScheduleByDay>
                </CalendarTrigger>
              </Triggers>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exe)}</Command>
                  <Arguments>{LaunchArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string ElementText(XElement parent, string name) =>
        parent.Element(Ns + name)?.Value.Trim() ?? "";

    /// <summary>schtasks 单次调用超时：卡死时终止进程并按失败处理，避免挂住调用方。</summary>
    private const int SchtasksTimeoutMs = 15_000;

    private static (int ExitCode, string StdOut, string StdErr) RunSchtasks(string args)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        p.Start();
        // 读原始字节自行解码：控制台程序重定向输出的编码随控制台代码页变化（GBK/UTF-8），
        // 固定按某一编码解码会得到乱码。优先严格 UTF-8，含 GBK 等非 UTF-8 字节时回退 OEM 代码页
        using var outMs = new MemoryStream();
        using var errMs = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(outMs);
        var errTask = p.StandardError.BaseStream.CopyToAsync(errMs);
        if (!p.WaitForExit(SchtasksTimeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 进程可能已自行退出 */ }
            throw new ScheduledTaskException("schtasks 调用超时");
        }
        outTask.Wait();
        errTask.Wait();
        return (p.ExitCode, Decode(outMs.ToArray()), Decode(errMs.ToArray()));
    }

    private static string Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes); }
            catch { return Encoding.UTF8.GetString(bytes); }
        }
    }

    private static string Describe(string stderr) =>
        string.IsNullOrWhiteSpace(stderr) ? "schtasks 调用失败" : stderr.Trim();
}
