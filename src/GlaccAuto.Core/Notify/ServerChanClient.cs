using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlaccAuto.Core.Notify;

/// <summary>
/// Server酱 Turbo 推送客户端。
/// 仅定时自动运行的收尾发送结果通知；
/// 发送失败一律静默返回 false，不影响领取主流程与退出逻辑。
/// </summary>
public static partial class ServerChanClient
{
    [GeneratedRegex(@"^SCT[0-9A-Za-z]+$")]
    private static partial Regex SendKeyPattern();

    private const string ApiBase = "https://sctapi.ftqq.com";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>SendKey 是否有效（非空且为 SCT 开头的字母数字串）；空即视为未启用。</summary>
    public static bool IsConfigured(string? sendKey) =>
        !string.IsNullOrWhiteSpace(sendKey) && SendKeyPattern().IsMatch(sendKey.Trim());

    /// <summary>发送一条消息；title 超过 32 字符自动截断，desp 支持 Markdown。</summary>
    public static async Task<bool> SendAsync(string sendKey, string title, string desp)
    {
        sendKey = sendKey.Trim();
        if (!SendKeyPattern().IsMatch(sendKey)) return false;
        if (title.Length > 32) title = title[..32];
        try
        {
            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("title", title),
                new KeyValuePair<string, string>("desp", desp),
            ]);
            using var resp = await Http.PostAsync($"{ApiBase}/{sendKey}.send", content);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() == 0;
        }
        catch
        {
            // 通知失败不影响主流程
            return false;
        }
    }
}
