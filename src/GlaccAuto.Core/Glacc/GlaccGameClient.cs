using System.Text;
using System.Text.Json;
using GlaccAuto.Core.Diagnostics;

namespace GlaccAuto.Core.Glacc;

/// <summary>
/// 业务主域客户端（任务列表 / 钱包 / mobileGLTaskPush 直推），对应 _reverse/scripts/push_test.py 的移植。
/// 请求头来自抓包 §四；任务配置每日可变，taskId/stageSum 必须从 mobileGLTaskList 动态读取。
/// 全部请求经 <see cref="GlaccSession"/> 执行：网络失败按预算重试，JWT 失效自动换新后重试。
/// </summary>
public sealed class GlaccGameClient
{
    private readonly GlaccCredentials _cred;
    private readonly GlaccSession _session;

    public GlaccGameClient(GlaccCredentials cred, GlaccSession session)
    {
        _cred = cred;
        _session = session;
    }

    /// <summary>主任务52 各阶段进度；失败时 <see cref="GlaccCallResult{T}.Reason"/> 给出原因。</summary>
    /// <param name="accept">结果有效性判定（对账场景应拒绝空列表）</param>
    public Task<GlaccCallResult<List<GlaccTaskStage>>> GetTaskStagesAsync(
        Func<List<GlaccTaskStage>?, bool>? accept = null,
        Func<int, TimeSpan, Task>? onRetry = null,
        CancellationToken ct = default)
    {
        if (!_cred.HasToken) return Task.FromResult(NotLoggedIn<List<GlaccTaskStage>>());
        var url = $"{GlaccConstants.GameBase}/xlppc.gacs/api/gxsdn/act/advert/mobileGLTaskList?appid={GlaccConstants.AppId}";
        return _session.RunAsync(c => SendAsync(HttpMethod.Get, url, null, c), ParseStages,
            accept, onRetry, "拉取任务进度", ct);
    }

    /// <summary>钱包分值（score，与"可用时长"按 80:1 分钟换算）。</summary>
    public Task<GlaccCallResult<long>> GetWalletScoreAsync(
        Func<int, TimeSpan, Task>? onRetry = null,
        CancellationToken ct = default)
    {
        if (!_cred.HasToken) return Task.FromResult(NotLoggedIn<long>());
        var url = $"{GlaccConstants.GameBase}/xlppc.gacs/api/gxsdn/gold/get_user_wallet";
        return _session.RunAsync(c => SendAsync(HttpMethod.Get, url, null, c), ParseWallet,
            null, onRetry, "查询余额", ct);
    }

    /// <summary>真签名直推一次 mobileGLTaskPush。code=0 发放成功；-1702 = 阶段已满；10007 = 签名校验失败。</summary>
    public Task<GlaccCallResult<GlaccPushResult>> PushTaskAsync(int taskId,
        Func<int, TimeSpan, Task>? onRetry = null,
        CancellationToken ct = default)
    {
        if (!_cred.HasToken) return Task.FromResult(NotLoggedIn<GlaccPushResult>());
        var url = $"{GlaccConstants.GameBase}/xlppc.gacs/api/gxsdn/act/advert/mobileGLTaskPush";
        return _session.RunAsync(
            c =>
            {
                var ts = GlaccCredentials.NowSeconds();
                var body = new GlaccPushBody(GlaccConstants.MasterTaskId, taskId, ts, [],
                    GlaccSign.PushSign(GlaccConstants.MasterTaskId, taskId, ts));
                return SendAsync(HttpMethod.Post, url,
                    JsonSerializer.Serialize(body, GlaccJsonContext.Default.GlaccPushBody), c);
            },
            ParsePush, null, onRetry, $"推送任务 {taskId}", ct);
    }

    // ── 响应解析（抛异常即视为请求失败，由执行器计入重试预算）──

    private static List<GlaccTaskStage> ParseStages(GlaccTlsResponse resp)
    {
        using var doc = JsonDocument.Parse(resp.Body);
        var list = doc.RootElement.GetProperty("data").GetProperty("list");
        foreach (var master in list.EnumerateArray())
        {
            if (!master.TryGetProperty("masterTaskId", out var mtid) ||
                !mtid.TryGetInt32(out var mid) || mid != GlaccConstants.MasterTaskId)
                continue;
            var stages = new List<GlaccTaskStage>();
            var i = 1;
            foreach (var t in master.GetProperty("taskDetailList").EnumerateArray())
            {
                var name = t.TryGetProperty("taskName", out var tn) && tn.GetString() is { Length: > 0 } n
                    ? n
                    : $"阶段{i}";
                stages.Add(new GlaccTaskStage(
                    t.GetProperty("taskId").GetInt32(),
                    name,
                    t.TryGetProperty("stageCurrent", out var sc) ? sc.GetInt32() : 0,
                    t.GetProperty("stageSum").GetInt32()));
                i++;
            }
            return stages;
        }
        return []; // 服务端今日未配置主任务52
    }

    private static long ParseWallet(GlaccTlsResponse resp)
    {
        using var doc = JsonDocument.Parse(resp.Body);
        return doc.RootElement.GetProperty("data").GetProperty("score").GetInt64();
    }

    private static GlaccPushResult ParsePush(GlaccTlsResponse resp)
    {
        using var doc = JsonDocument.Parse(resp.Body);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        long addScore = 0;
        var taskName = "";
        if (code == 0 && root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("addScore", out var score))
                addScore = score.ValueKind == JsonValueKind.Number ? score.GetInt64() : 0;
            if (data.TryGetProperty("taskName", out var tn) && tn.ValueKind == JsonValueKind.String)
                taskName = tn.GetString() ?? "";
        }
        return new GlaccPushResult(code, addScore, taskName);
    }

    /// <summary>本地无凭证的短路结果：与网络失败区分，供上层引导登录而非提示网络异常。</summary>
    private static GlaccCallResult<T> NotLoggedIn<T>() =>
        GlaccCallResult<T>.Fail(GlaccFailReason.NotLoggedIn);

    // ── HTTP 基础设施（game-xacc 通用头，抓包 §四；走 GlaccTls：OkHttp/Android TLS 指纹）──

    private async Task<GlaccHttpResponse> SendAsync(HttpMethod method, string url, string? json,
        CancellationToken ct)
    {
        try
        {
            var device = _cred.Device;
            var headers = new Dictionary<string, string>
            {
                ["appid"] = GlaccConstants.AppId,
                ["package_name"] = GlaccConstants.PackageName,
                ["package-name"] = GlaccConstants.PackageName,
                ["x-client-type"] = "glacc_android",
                ["userid"] = _cred.Sub,
                ["user-id"] = _cred.Sub,
                ["app-version"] = GlaccConstants.AppVersion,
                ["peerid"] = _cred.PeerId,
                ["install-channel"] = "official",
                ["x-channel-id"] = "official",
                ["x-device-id"] = _cred.PeerId,
                ["x-guid"] = _cred.PeerId,
                ["authorization"] = $"Bearer {_cred.AccessToken}",
                ["client_version"] = GlaccConstants.AppVersion,
                ["user-agent"] = GlaccUa.Game(device),
            };
            if (json is not null) headers["content-type"] = "application/json";
            var payload = new TlsRequestPayload(
                GlaccDevicePool.IdentifierFor(device), method.Method, url, json,
                headers, [.. headers.Keys], 15, true, true, false, false, true);
            // 原生库的失败原因必须带出来：这是区分"网络不可达"与"本机库异常"的唯一线索
            string error = "";
            var resp = await Task.Run(() =>
            {
                var result = GlaccTls.Send(payload, out var e);
                error = e;
                return result;
            }, ct);
            return new GlaccHttpResponse(resp, error);
        }
        catch (GlaccTlsUnavailableException ex)
        {
            DiagLog.Error("TLS 指纹库不可用", ex);
            return GlaccHttpResponse.FromClientFault(GlaccSession.Describe(ex));
        }
        catch (Exception ex)
        {
            return new GlaccHttpResponse(null, GlaccSession.Describe(ex));
        }
    }
}
