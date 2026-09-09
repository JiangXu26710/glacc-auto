using System.Text.Json.Serialization;

namespace GlaccAuto.Core.Glacc;

/// <summary>操作结果（带用户可读错误信息）。</summary>
public sealed class GlaccResult
{
    public bool Ok { get; init; }
    /// <summary>true = refresh_token 已失效（invalid_grant/4126），需重新短信登录</summary>
    public bool NeedRelogin { get; init; }
    public string Error { get; init; } = "";

    public static GlaccResult Success() => new() { Ok = true };
    public static GlaccResult Fail(string error, bool needRelogin = false) =>
        new() { Ok = false, Error = error, NeedRelogin = needRelogin };
}

/// <summary>主任务52 阶段进度（来自 mobileGLTaskList，任务配置每日可变，禁止硬编码）。</summary>
public sealed record GlaccTaskStage(int TaskId, string Name, int StageCurrent, int StageSum);

/// <summary>mobileGLTaskPush 响应（code=0 时 data.addScore 为本次发放分值）。</summary>
public sealed record GlaccPushResult(int Code, long AddScore, string TaskName);
