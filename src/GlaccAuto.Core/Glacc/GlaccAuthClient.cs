using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GlaccAuto.Core.Diagnostics;

namespace GlaccAuto.Core.Glacc;

/// <summary>登录态管理（refresh / 短信登录），对应 _reverse/scripts/auth.py 的移植。</summary>
public sealed class GlaccAuthClient
{
    private readonly GlaccCredentials _cred;

    public GlaccAuthClient(GlaccCredentials cred)
    {
        _cred = cred;
    }

    // ── 对外操作 ──

    /// <summary>发送短信验证码（captcha 直签发 + verification）。成功后 verification_id 存入凭证。</summary>
    public async Task<GlaccResult> SendSmsAsync(string phoneInput, CancellationToken ct = default)
    {
        var phone = NormalizePhone(phoneInput);
        if (phone is null) return GlaccResult.Fail("手机号格式不正确（应为 11 位大陆手机号）");

        // 设备标识与设备档案由手机号派生，captcha 请求起就要用：先定下当前登录目标。
        // 只记 PendingPhone、不动 Phone：登录成功才转正，改号登录期间不影响已登录会话的显示与身份
        _cred.PendingPhone = phone;
        _cred.Save();

        var captcha = await CaptchaInitAsync("POST:/v1/auth/verification", phone, ct);
        if (captcha is null)
        {
            DiagLog.Warn("发送验证码失败：未取得人机凭证");
            return GlaccResult.Fail("获取人机凭证失败，请稍后重试");
        }

        var resp = await PostJsonAsync($"{GlaccConstants.AuthBase}/v1/auth/verification",
            new VerificationRequest(captcha, GlaccConstants.ClientId, phone, "ANY", "SIGN_IN"),
            GlaccJsonContext.Default.VerificationRequest, ct, loginFlow: true);
        if (resp.Body is null)
        {
            DiagLog.Warn($"发送验证码失败（网络层）：{resp.Error}");
            return GlaccResult.Fail("网络错误，发送验证码失败");
        }
        using var doc = TryParse(resp.Body);
        if (doc is null)
        {
            DiagLog.Warn($"发送验证码失败：响应非 JSON（HTTP {resp.Status}）");
            return GlaccResult.Fail("发送验证码失败：服务端响应异常");
        }

        var root = doc.RootElement;
        if (!root.TryGetProperty("verification_id", out var vid))
        {
            var msg = DescribeError(root);
            DiagLog.Warn($"发送验证码失败：{msg}");
            return GlaccResult.Fail($"发送验证码失败：{msg}");
        }
        _cred.VerificationId = vid.GetString() ?? "";
        _cred.VerificationIdAt = GlaccCredentials.NowSeconds();
        _cred.Save();
        return GlaccResult.Success();
    }

    /// <summary>用短信验证码完成登录（verify → captcha → signin），成功后凭证入库。</summary>
    public async Task<GlaccResult> LoginAsync(string code, CancellationToken ct = default)
    {
        var vid = _cred.VerificationId;
        if (string.IsNullOrEmpty(vid)) return GlaccResult.Fail("请先发送验证码");
        if (GlaccCredentials.NowSeconds() - _cred.VerificationIdAt > 280)
            return GlaccResult.Fail("验证码已过期（5 分钟），请重新发送");

        // 1) 验证码换 verification_token
        var vResp = await PostJsonAsync($"{GlaccConstants.AuthBase}/v1/auth/verification/verify",
            new VerifyCodeRequest(GlaccConstants.ClientId, vid, code),
            GlaccJsonContext.Default.VerifyCodeRequest, ct, loginFlow: true);
        if (vResp.Body is null)
        {
            DiagLog.Warn($"验证码校验失败（网络层）：{vResp.Error}");
            return GlaccResult.Fail("网络错误，验证码校验失败");
        }
        string verificationToken;
        using (var doc = TryParse(vResp.Body))
        {
            if (doc is null)
            {
                DiagLog.Warn($"验证码校验失败：响应非 JSON（HTTP {vResp.Status}）");
                return GlaccResult.Fail("验证码校验失败：服务端响应异常");
            }
            if (!doc.RootElement.TryGetProperty("verification_token", out var vt))
            {
                var msg = DescribeError(doc.RootElement);
                DiagLog.Warn($"验证码校验失败：{msg}");
                return GlaccResult.Fail($"验证码校验失败：{msg}");
            }
            verificationToken = vt.GetString() ?? "";
        }

        // 2) signin（signin action 需要自己的 captcha_token）
        var captcha = await CaptchaInitAsync("POST:/v1/auth/signin", _cred.LoginPhone, ct);
        if (captcha is null)
        {
            DiagLog.Warn("登录失败：未取得人机凭证");
            return GlaccResult.Fail("获取人机凭证失败，请稍后重试");
        }

        var sResp = await PostJsonAsync($"{GlaccConstants.AuthBase}/v1/auth/signin",
            new SigninRequest(captcha, GlaccConstants.ClientId, GlaccConstants.ClientSecret,
                _cred.LoginPhone, verificationToken),
            GlaccJsonContext.Default.SigninRequest, ct, loginFlow: true);
        if (sResp.Body is null)
        {
            DiagLog.Warn($"登录失败（网络层）：{sResp.Error}");
            return GlaccResult.Fail("网络错误，登录失败");
        }
        using var sDoc = TryParse(sResp.Body);
        if (sDoc is null)
        {
            DiagLog.Warn($"登录失败：响应非 JSON（HTTP {sResp.Status}）");
            return GlaccResult.Fail("登录失败：服务端响应异常");
        }

        var sRoot = sDoc.RootElement;
        if (!sRoot.TryGetProperty("access_token", out var at))
        {
            var msg = DescribeError(sRoot);
            DiagLog.Warn($"登录失败：{msg}");
            return GlaccResult.Fail($"登录失败：{msg}");
        }

        _cred.AccessToken = at.GetString() ?? "";
        if (sRoot.TryGetProperty("refresh_token", out var rt))
            _cred.RefreshToken = rt.GetString() ?? _cred.RefreshToken;
        if (sRoot.TryGetProperty("sub", out var sub))
            _cred.Sub = sub.GetString() ?? "";
        _cred.ObtainedAt = GlaccCredentials.NowSeconds();
        _cred.ExpiresIn = sRoot.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var sec)
            ? sec : 7200;
        // 登录完成：目标手机号转正，清理一次性状态
        _cred.Phone = _cred.LoginPhone;
        _cred.PendingPhone = "";
        _cred.VerificationId = "";
        _cred.VerificationIdAt = 0;
        _cred.Save();
        return GlaccResult.Success();
    }

    /// <summary>
    /// 用 refresh_token 换新 JWT（端点 POST /v1/auth/token，JSON grant_type）。
    /// 网络层失败（无响应/5xx/网关错误页）标记为可重试，由调用方按重试预算退避重试；
    /// invalid_grant 是明确的业务判定，不重试。
    /// </summary>
    public async Task<GlaccResult> RefreshAsync(CancellationToken ct = default)
    {
        var rt = _cred.RefreshToken;
        if (string.IsNullOrEmpty(rt))
        {
            DiagLog.Warn("刷新登录态失败：本地无 refresh_token");
            return GlaccResult.Fail("无 refresh_token，请重新登录");
        }

        var resp = await PostJsonAsync($"{GlaccConstants.AuthBase}/v1/auth/token",
            new TokenRefreshRequest("refresh_token", rt, GlaccConstants.ClientId,
                GlaccConstants.ClientSecret),
            GlaccJsonContext.Default.TokenRefreshRequest, ct);
        if (resp.Transient)
        {
            DiagLog.Warn($"刷新登录态失败（网络层）：{resp.Error}");
            return GlaccResult.Fail("网络错误，刷新登录态失败", retryable: true);
        }
        using var doc = TryParse(resp.Body);
        if (doc is null)
        {
            DiagLog.Warn($"刷新登录态失败：响应非 JSON（HTTP {resp.Status}）");
            return GlaccResult.Fail("刷新登录态失败：服务端响应异常", retryable: true);
        }

        var root = doc.RootElement;
        if (!root.TryGetProperty("access_token", out var at))
        {
            // invalid_grant / 4126 = refresh_token 已失效，需重新短信登录
            var invalid = root.TryGetProperty("error", out var err) &&
                          err.GetString()?.Contains("invalid_grant") == true;
            DiagLog.Warn(invalid
                ? "refresh_token 已失效，需重新短信登录"
                : $"刷新登录态失败：{DescribeError(root)}");
            return GlaccResult.Fail(
                invalid ? "登录已过期，请重新短信登录" : $"刷新登录态失败：{DescribeError(root)}",
                needRelogin: invalid);
        }
        _cred.AccessToken = at.GetString() ?? "";
        if (root.TryGetProperty("refresh_token", out var nrt))
            _cred.RefreshToken = nrt.GetString() ?? rt; // 服务端可能轮换 refresh_token
        _cred.ObtainedAt = GlaccCredentials.NowSeconds();
        _cred.ExpiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var sec)
            ? sec : 7200;
        _cred.Save();
        return GlaccResult.Success();
    }

    // ── 内部步骤 ──

    /// <summary>雷盾 captcha_token 直签发（无人工挑战）；失败返回 null。</summary>
    private async Task<string?> CaptchaInitAsync(string action, string phone, CancellationToken ct)
    {
        var resp = await PostJsonAsync(
            $"{GlaccConstants.AuthBase}/v1/shield/captcha/init?client_id={GlaccConstants.ClientId}",
            new CaptchaInitRequest(action, GlaccConstants.ClientId, _cred.LoginDeviceId,
                new CaptchaMeta(phone), GlaccConstants.RedirectUri),
            GlaccJsonContext.Default.CaptchaInitRequest, ct, loginFlow: true);
        if (resp.Body is null) return null;
        using var doc = TryParse(resp.Body);
        return doc is not null && doc.RootElement.TryGetProperty("captcha_token", out var t)
            ? t.GetString() : null;
    }

    /// <summary>
    /// 认证链路的请求主体（走 GlaccTls：OkHttp/Android TLS 指纹 + 设备档案 UA）。
    /// 短信登录流程（发码/验码/signin）传 loginFlow = true：设备身份跟随待登录手机号；
    /// 其余（refresh）跟随已登录账号，改号登录进行中不影响既有会话的请求身份。
    /// </summary>
    private async Task<AuthHttpResponse> PostJsonAsync<T>(string url, T payload,
        JsonTypeInfo<T> typeInfo, CancellationToken ct, bool loginFlow = false)
    {
        try
        {
            var device = loginFlow ? _cred.LoginDevice : _cred.Device;
            var headers = new Dictionary<string, string>
            {
                ["x-device-id"] = loginFlow ? _cred.LoginDeviceId : _cred.DeviceId,
                ["user-agent"] = GlaccUa.Auth(device),
                ["accept-language"] = "zh-CN",
                ["content-type"] = "application/json; charset=utf-8",
            };
            var body = JsonSerializer.Serialize(payload, typeInfo);
            var tls = new TlsRequestPayload(
                GlaccDevicePool.IdentifierFor(device), "POST", url, body,
                headers, [.. headers.Keys], 15, true, true, false, false, true);
            // 原生库的失败原因带出来落日志：认证链路信息量最大的排障线索
            string error = "";
            var resp = await Task.Run(() =>
            {
                var result = GlaccTls.Send(tls, out var e);
                error = e;
                return result;
            }, ct);
            if (resp is null) DiagLog.Warn($"认证接口无响应（{Endpoint(url)}）：{error}");
            return new AuthHttpResponse(resp?.Body, resp?.Status ?? 0, error);
        }
        catch (GlaccTlsUnavailableException ex)
        {
            DiagLog.Error("TLS 指纹库不可用", ex);
            return new AuthHttpResponse(null, 0, GlaccSession.Describe(ex));
        }
        catch (Exception ex)
        {
            return new AuthHttpResponse(null, 0, GlaccSession.Describe(ex));
        }
    }

    /// <summary>日志用端点名：只保留路径，不带查询参数。</summary>
    private static string Endpoint(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;

    /// <summary>解析响应体；非 JSON（如网关错误页）返回 null，避免异常外泄到业务层。</summary>
    private static JsonDocument? TryParse(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            return JsonDocument.Parse(body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>一次 POST 的原始结果：<see cref="Body"/> 为 null 表示未取得响应（网络层失败）。</summary>
    private readonly record struct AuthHttpResponse(string? Body, int Status, string Error = "")
    {
        /// <summary>未取得响应或服务端 5xx：网络层失败，未产生业务判定。</summary>
        public bool Transient => Body is null || Status >= 500;
    }

    private static string DescribeError(JsonElement root)
    {
        // 尽量提取 error / error_description / message / code
        if (root.ValueKind != JsonValueKind.Object) return root.GetRawText() is { Length: > 0 } s ? s : "未知错误";
        foreach (var key in new[] { "error_description", "error", "message", "msg" })
        {
            if (root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
                v.GetString() is { Length: > 0 } s)
                return s;
        }
        if (root.TryGetProperty("code", out var code))
            return $"code={code.GetRawText()}";
        return "未知错误";
    }

    /// <summary>11 位手机号 → "+86 12xxxxxxxxx"（与官方客户端一致的格式）。</summary>
    public static string? NormalizePhone(string input)
    {
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length == 13 && digits.StartsWith("86")) digits = digits[2..];
        return digits.Length == 11 && digits.StartsWith('1') ? $"+86 {digits}" : null;
    }
}

// ── 请求 DTO（source-gen 序列化，AOT 安全）──

internal record TokenRefreshRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret);

internal record CaptchaMeta([property: JsonPropertyName("phone_number")] string PhoneNumber);

internal record CaptchaInitRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("meta")] CaptchaMeta Meta,
    [property: JsonPropertyName("redirect_uri")] string RedirectUri);

internal record VerificationRequest(
    [property: JsonPropertyName("captcha_token")] string CaptchaToken,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("phone_number")] string PhoneNumber,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("usage")] string Usage);

internal record VerifyCodeRequest(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("verification_id")] string VerificationId,
    [property: JsonPropertyName("verification_code")] string VerificationCode);

internal record SigninRequest(
    [property: JsonPropertyName("captcha_token")] string CaptchaToken,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("verification_token")] string VerificationToken);
