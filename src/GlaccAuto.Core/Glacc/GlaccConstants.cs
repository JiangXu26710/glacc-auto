namespace GlaccAuto.Core.Glacc;

/// <summary>
/// 官方客户端常量。
/// 用户个人信息（手机号/token/设备标识）在 <see cref="GlaccCredentials"/>，持久化于本机 %APPDATA%。
/// </summary>
public static class GlaccConstants
{
    // ── 服务端域名 ──
    /// <summary>账号/登录域（xbase OAuth）</summary>
    public const string AuthBase = "https://user.geilijiasu.net";
    /// <summary>业务主域（任务/钱包/推送）</summary>
    public const string GameBase = "https://game-xacc.xunlei.com";

    // ── OAuth 客户端（JS 字符串表明文）──
    public const string ClientId = "abjIHOBRMk89gOKc";
    public const string ClientSecret = "BihWgSGwEfGC_IKQXAAUlQ";
    public const string RedirectUri = "xlaccsdk01://xunlei.com/callback?state=harbor";

    // ── mobileGLTaskPush 签名盐（Hermes JS module 1591 adStatsSignParams）──
    public const string SignKey = "07f21229eab0bb7d7c4";

    // ── 客户端版本指纹 ──
    public const string AppVersion = "1.26.8.2";
    public const string AppId = "8";
    public const string PackageName = "glacc";
    public const int MasterTaskId = 52; // 主任务「移动端-点广告得时长」

    // UA 不写死：由 GlaccUa 模板 + 设备档案（credentials.json）生成，
    // 登录时按手机号哈希从 GlaccDevicePool 确定性选档，避免全局同一设备指纹。

    // ── 服务端业务码（实测所得）──
    /// <summary>登录校验失败：JWT 无效/过期/缺失。HTTP 仍为 200，仅 body.code 区分（钱包与 push 已实测）。</summary>
    public const int AuthErrorCode = 10003;
    /// <summary>push 请求参数无效（如 taskId 不存在）。鉴权校验先于参数校验。</summary>
    public const int InvalidParamCode = 10053;

    // ── 换算 ──
    /// <summary>钱包 score → 可加速时长：80 score = 1 分钟</summary>
    public const double ScorePerMinute = 80.0;
}
