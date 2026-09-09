using System.Text.Json.Serialization;

namespace GlaccAuto.Core.Glacc;

/// <summary>source-gen JSON 上下文（PublishAot 必需，禁用反射序列化）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GlaccCredentials))]
[JsonSerializable(typeof(GlaccDeviceProfile))]
[JsonSerializable(typeof(TokenRefreshRequest))]
[JsonSerializable(typeof(CaptchaInitRequest))]
[JsonSerializable(typeof(VerificationRequest))]
[JsonSerializable(typeof(VerifyCodeRequest))]
[JsonSerializable(typeof(SigninRequest))]
[JsonSerializable(typeof(GlaccPushBody))]
[JsonSerializable(typeof(TlsRequestPayload))]
internal partial class GlaccJsonContext : JsonSerializerContext;

/// <summary>mobileGLTaskPush 请求体（extData 不参与签名，保持空对象）。</summary>
internal record GlaccPushBody(
    [property: JsonPropertyName("masterTaskId")] int MasterTaskId,
    [property: JsonPropertyName("taskId")] int TaskId,
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("extData")] Dictionary<string, string> ExtData,
    [property: JsonPropertyName("sign")] string Sign);
