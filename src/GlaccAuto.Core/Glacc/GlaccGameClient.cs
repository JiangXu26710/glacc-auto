using System.Text;
using System.Text.Json;

namespace GlaccAuto.Core.Glacc;

/// <summary>
/// 业务主域客户端（任务列表 / 钱包 / mobileGLTaskPush 直推），对应 _reverse/scripts/push_test.py 的移植。
/// 请求头来自抓包 §四；任务配置每日可变，taskId/stageSum 必须从 mobileGLTaskList 动态读取。
/// </summary>
public sealed class GlaccGameClient
{
    private readonly GlaccCredentials _cred;

    public GlaccGameClient(GlaccCredentials cred)
    {
        _cred = cred;
    }

    /// <summary>主任务52 各阶段进度；未登录或网络失败返回 null。</summary>
    public async Task<List<GlaccTaskStage>?> GetTaskStagesAsync(CancellationToken ct = default)
    {
        if (!_cred.HasToken) return null;
        var resp = await SendAsync(HttpMethod.Get,
            $"{GlaccConstants.GameBase}/xlppc.gacs/api/gxsdn/act/advert/mobileGLTaskList?appid={GlaccConstants.AppId}",
            null, ct);
        if (resp is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(resp);
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
        catch (Exception)
        {
            return null; // 响应结构异常
        }
    }

    /// <summary>钱包分值（score，与"可用时长"按 80:1 分钟换算）；失败返回 null。</summary>
    public async Task<long?> GetWalletScoreAsync(CancellationToken ct = default)
    {
        if (!_cred.HasToken) return null;
        var resp = await SendAsync(HttpMethod.Get,
            $"{GlaccConstants.GameBase}/xlppc.gacs/api/gxsdn/gold/get_user_wallet", null, ct);
        if (resp is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(resp);
            return doc.RootElement.GetProperty("data").GetProperty("score").GetInt64();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>真签名直推一次 mobileGLTaskPush。code=0 发放成功；-1702 = 阶段已满；10007 = 签名校验失败。</summary>
    public async Task<GlaccPushResult?> PushTaskAsync(int taskId, CancellationToken ct = default)
    {
        if (!_cred.HasToken) return null;
        var ts = GlaccCredentials.NowSeconds();
        var body = new GlaccPushBody(GlaccConstants.MasterTaskId, taskId, ts, [],
            GlaccSign.PushSign(GlaccConstants.MasterTaskId, taskId, ts));
        var resp = await SendAsync(HttpMethod.Post,
            $"{GlaccConstants.GameBase}/xlppc.gacs/api/gxsdn/act/advert/mobileGLTaskPush",
            JsonSerializer.Serialize(body, GlaccJsonContext.Default.GlaccPushBody), ct);
        if (resp is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            long addScore = 0;
            var taskName = "";
            if (code == 0 && root.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("addScore", out var score))
                {
                    addScore = score.ValueKind == JsonValueKind.Number ? score.GetInt64() : 0;
                }
                if (data.TryGetProperty("taskName", out var tn) && tn.ValueKind == JsonValueKind.String)
                    taskName = tn.GetString() ?? "";
            }
            return new GlaccPushResult(code, addScore, taskName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── HTTP 基础设施（game-xacc 通用头，抓包 §四；走 GlaccTls：OkHttp/Android TLS 指纹）──

    private async Task<string?> SendAsync(HttpMethod method, string url, string? json, CancellationToken ct)
    {
        try
        {
            var device = _cred.DeviceOrDefault;
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
            var resp = await Task.Run(() => GlaccTls.Send(
                new TlsRequestPayload(
                    GlaccDevicePool.IdentifierFor(device), method.Method, url, json,
                    headers, [.. headers.Keys], 15, true, true, false, false, true),
                out _), ct);
            return resp?.Body;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
