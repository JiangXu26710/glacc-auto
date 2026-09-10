using System.Text.Json;

namespace GlaccAuto.Core.Glacc;

/// <summary>业务请求失败原因。</summary>
public enum GlaccFailReason
{
    /// <summary>请求成功</summary>
    None,
    /// <summary>网络/服务端临时故障，重试预算耗尽</summary>
    Network,
    /// <summary>登录态无法续期（refresh_token 失效），需重新短信登录</summary>
    NeedRelogin,
    /// <summary>本地无登录凭证：尚未登录，需先完成短信登录</summary>
    NotLoggedIn,
}

/// <summary>统一请求结果：<see cref="Value"/> 仅在 <see cref="Ok"/> 时有效。</summary>
public readonly record struct GlaccCallResult<T>(T? Value, GlaccFailReason Reason)
{
    public bool Ok => Reason == GlaccFailReason.None;

    public static GlaccCallResult<T> Success(T value) => new(value, GlaccFailReason.None);
    public static GlaccCallResult<T> Fail(GlaccFailReason reason) => new(default, reason);
}

/// <summary>
/// 业务请求公共执行器（网络重试 + 登录态续期保底）。
/// 网络失败与服务端 5xx 按配置预算退避重试；JWT 失效（401/403）自动换新并重试同一请求；
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
    /// <param name="send">发出请求（返回 null = 网络层失败）</param>
    /// <param name="parse">解析响应（抛异常或返回 null = 视为失败）</param>
    /// <param name="accept">结果有效性判定（null 表示非空即有效；如对账时空任务列表视为失败）</param>
    /// <param name="onRetry">重试前回调（重试序号、等待时长），供上层更新提示</param>
    public async Task<GlaccCallResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<GlaccTlsResponse?>> send,
        Func<GlaccTlsResponse, T?> parse,
        Func<T?, bool>? accept = null,
        Func<int, TimeSpan, Task>? onRetry = null,
        CancellationToken ct = default)
    {
        var maxAttempts = Math.Clamp(_retryCount(), 0, 10) + 1;
        var jwtRefreshed = false;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var resp = await send(ct);
            if (resp is null || resp.Status >= 500)
            {
                if (attempt < maxAttempts)
                {
                    await WaitRetryAsync(attempt, onRetry, ct);
                    continue;
                }
                return GlaccCallResult<T>.Fail(GlaccFailReason.Network);
            }

            if (IsAuthFailed(resp))
            {
                // 换新后仍被拒 = 登录态不可用，只能重新短信登录
                if (jwtRefreshed) return GlaccCallResult<T>.Fail(GlaccFailReason.NeedRelogin);
                jwtRefreshed = true;
                var refreshed = await EnsureJwtAsync(force: true, ct);
                if (!refreshed.Ok)
                {
                    return GlaccCallResult<T>.Fail(
                        refreshed.NeedRelogin ? GlaccFailReason.NeedRelogin : GlaccFailReason.Network);
                }
                attempt--; // 换新成功：重试本请求，不占重试预算
                continue;
            }

            T? value;
            try
            {
                value = parse(resp);
            }
            catch
            {
                value = default;
            }
            if (value is null || (accept is not null && !accept(value)))
            {
                if (attempt < maxAttempts)
                {
                    await WaitRetryAsync(attempt, onRetry, ct);
                    continue;
                }
                return GlaccCallResult<T>.Fail(GlaccFailReason.Network);
            }
            return GlaccCallResult<T>.Success(value);
        }

        return GlaccCallResult<T>.Fail(GlaccFailReason.Network);
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
