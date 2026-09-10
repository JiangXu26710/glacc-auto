using System.Text.Json;
using GlaccAuto.Core.Diagnostics;

namespace GlaccAuto.Core.Glacc;

/// <summary>业务请求失败原因。</summary>
public enum GlaccFailReason
{
    /// <summary>请求成功</summary>
    None,
    /// <summary>未取得响应：网络不可达、超时</summary>
    NoResponse,
    /// <summary>服务端 5xx：服务端临时故障</summary>
    ServerError,
    /// <summary>取得响应但无法解析或不符合预期：接口可能已变更</summary>
    BadResponse,
    /// <summary>客户端自身故障：TLS 指纹库缺失或无法加载</summary>
    ClientError,
    /// <summary>登录态无法续期（refresh_token 失效），需重新短信登录</summary>
    NeedRelogin,
    /// <summary>本地无登录凭证：尚未登录，需先完成短信登录</summary>
    NotLoggedIn,
}

/// <summary>
/// 一次请求的传输结果：<see cref="Response"/> 为 null 表示未取得响应，
/// 此时 <see cref="Error"/> 给出技术细节（异常类型与信息），供日志与诊断展示。
/// </summary>
public readonly record struct GlaccHttpResponse(
    GlaccTlsResponse? Response,
    string Error = "",
    GlaccFailReason Reason = GlaccFailReason.NoResponse)
{
    /// <summary>TLS 指纹库不可用（与网络故障区分）</summary>
    public static GlaccHttpResponse FromClientFault(string error) =>
        new(null, error, GlaccFailReason.ClientError);
}

/// <summary>
/// 统一请求结果：<see cref="Value"/> 仅在 <see cref="Ok"/> 时有效。
/// <see cref="Detail"/> 为技术细节，不作为面向用户的主提示，供日志与"复制错误信息"使用。
/// </summary>
public readonly record struct GlaccCallResult<T>(T? Value, GlaccFailReason Reason, string Detail = "")
{
    public bool Ok => Reason == GlaccFailReason.None;

    public static GlaccCallResult<T> Success(T value) => new(value, GlaccFailReason.None);

    public static GlaccCallResult<T> Fail(GlaccFailReason reason, string detail = "") =>
        new(default, reason, detail);
}

/// <summary>
/// 业务请求公共执行器（网络重试 + 登录态续期保底）。
/// 网络失败与服务端 5xx 按配置预算退避重试；JWT 失效（body code 10003）自动换新并重试同一请求；
/// refresh_token 已失效（需短信验证码）时返回 <see cref="GlaccFailReason.NeedRelogin"/>，
/// 由上层决定是否中断业务并引导重新登录。
/// </summary>
public sealed class GlaccSession
{
    /// <summary>refresh_token 保活间隔：长期不调用会失效（invalid_grant/4126），故到期前主动换新一次。</summary>
    private const long RefreshKeepAliveSeconds = 7 * 24 * 3600;

    private readonly GlaccCredentials _cred;
    private readonly GlaccAuthClient _auth;
    private readonly Func<int> _retryCount;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <param name="retryCount">每个请求失败后的额外重试次数（运行时读取，设置页可即时生效）</param>
    public GlaccSession(GlaccCredentials cred, GlaccAuthClient auth, Func<int> retryCount)
    {
        _cred = cred;
        _auth = auth;
        _retryCount = retryCount;
    }

    /// <summary>登录态需要换新：JWT 已过期（留 60s 余量），或距上次刷新超过保活间隔。</summary>
    public bool JwtNeedsRefresh =>
        !_cred.IsJwtValid ||
        GlaccCredentials.NowSeconds() - _cred.ObtainedAt > RefreshKeepAliveSeconds;

    /// <summary>异常 → 一行可读技术细节（类型 + 信息）。</summary>
    public static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// 确保 JWT 可用：需要时才打 refresh 端点；force = 无视过期判断强制换新。
    /// 网络层失败（无响应/5xx）按重试预算退避重试——刷新多发生在启动与定时拉起路径，
    /// 无人值守，不能因一次瞬断就判登录不可用；invalid_grant 等业务判定立即返回、不消耗预算。
    /// </summary>
    public async Task<GlaccResult> EnsureJwtAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && !JwtNeedsRefresh) return GlaccResult.Success();
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (!force && !JwtNeedsRefresh) return GlaccResult.Success();
            var maxAttempts = Math.Clamp(_retryCount(), 0, 10) + 1;
            for (var attempt = 1; ; attempt++)
            {
                var result = await _auth.RefreshAsync(ct);
                if (result.Ok || !result.Retryable || attempt >= maxAttempts) return result;
                DiagLog.Warn($"刷新登录态第 {attempt} 次失败：{result.Error}");
                await Task.Delay(BackoffDelay(attempt - 1), ct);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// 执行一次业务请求：失败按重试预算退避重试，JWT 失效自动换新后重试（不消耗预算）。
    /// </summary>
    /// <param name="send">发出请求（Response 为 null = 未取得响应）</param>
    /// <param name="parse">解析响应（抛异常或返回 null = 视为失败）</param>
    /// <param name="accept">结果有效性判定（null 表示非空即有效；如对账时空任务列表视为失败）</param>
    /// <param name="onRetry">重试前回调（重试序号、等待时长），供上层更新提示</param>
    /// <param name="what">业务描述，仅用于日志</param>
    public async Task<GlaccCallResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<GlaccHttpResponse>> send,
        Func<GlaccTlsResponse, T?> parse,
        Func<T?, bool>? accept = null,
        Func<int, TimeSpan, Task>? onRetry = null,
        string what = "请求",
        CancellationToken ct = default)
    {
        var maxAttempts = Math.Clamp(_retryCount(), 0, 10) + 1;
        var jwtRefreshed = false;
        var reason = GlaccFailReason.NoResponse;
        var detail = "";
        var attemptsMade = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            attemptsMade = attempt;
            var http = await send(ct);
            var resp = http.Response;

            if (resp is null)
            {
                reason = http.Reason;
                detail = http.Error.Length > 0 ? http.Error : "未取得响应";
            }
            else if (resp.Status >= 500)
            {
                reason = GlaccFailReason.ServerError;
                detail = $"HTTP {resp.Status}";
            }
            else if (IsAuthFailed(resp))
            {
                // 换新后仍被拒 = 登录态不可用，只能重新短信登录
                if (jwtRefreshed)
                {
                    DiagLog.Warn($"{what}：登录态换新后仍被服务端拒绝");
                    return GlaccCallResult<T>.Fail(GlaccFailReason.NeedRelogin, "登录态换新后仍被服务端拒绝");
                }
                jwtRefreshed = true;
                var refreshed = await EnsureJwtAsync(force: true, ct);
                if (!refreshed.Ok)
                {
                    var loginReason = refreshed.NeedRelogin
                        ? GlaccFailReason.NeedRelogin
                        : GlaccFailReason.NoResponse;
                    DiagLog.Warn($"{what}：登录态换新失败（{refreshed.Error}）");
                    return GlaccCallResult<T>.Fail(loginReason, refreshed.Error);
                }
                attempt--; // 换新成功：重试本请求，不占重试预算
                continue;
            }
            else
            {
                T? value;
                var parseError = "";
                try
                {
                    value = parse(resp);
                }
                catch (Exception ex)
                {
                    value = default;
                    parseError = Describe(ex);
                }

                if (value is not null && (accept is null || accept(value)))
                    return GlaccCallResult<T>.Success(value);

                reason = GlaccFailReason.BadResponse;
                detail = value is null
                    ? parseError.Length > 0 ? parseError : "响应内容无法解析"
                    : "响应内容不符合预期";
            }

            // 客户端环境故障（如指纹库缺失）重试不会改变结果，立即按终态返回
            if (reason == GlaccFailReason.ClientError) break;

            if (attempt >= maxAttempts) break;
            DiagLog.Warn($"{what}第 {attempt} 次尝试失败（{reason}）：{detail}");
            await WaitRetryAsync(attempt, onRetry, ct);
        }

        DiagLog.Error($"{what}失败：{reason}（已尝试 {attemptsMade} 次）｜{detail}");
        return GlaccCallResult<T>.Fail(reason, detail);
    }

    /// <summary>
    /// 登录态是否失效。实测：服务端一律返回 HTTP 200，
    /// JWT 无效/过期/缺失时由 body.code = 10003 表达（HTTP 401/403 仅作兜底）。
    /// 注：任务列表接口不校验 JWT，失效时仍正常返回，故只有钱包与 push 会触发续期。
    /// </summary>
    private static bool IsAuthFailed(GlaccTlsResponse resp)
    {
        if (resp.Status is 401 or 403) return true;
        try
        {
            using var doc = JsonDocument.Parse(resp.Body);
            return doc.RootElement.TryGetProperty("code", out var c) &&
                   c.TryGetInt32(out var code) && code == GlaccConstants.AuthErrorCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitRetryAsync(int attempt, Func<int, TimeSpan, Task>? onRetry,
        CancellationToken ct)
    {
        var wait = BackoffDelay(attempt - 1);
        if (onRetry is not null) await onRetry(attempt, wait);
        await Task.Delay(wait, ct);
    }

    /// <summary>退避间隔：2s 起逐次翻倍，封顶 64s（2,4,8,16,32,64,64…）。</summary>
    public static TimeSpan BackoffDelay(int retryIndex) =>
        TimeSpan.FromSeconds(Math.Min(64, 2 << Math.Clamp(retryIndex, 0, 30)));
}
