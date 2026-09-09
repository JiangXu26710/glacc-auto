using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlaccAuto.Core.Glacc;

/// <summary>
/// 用户级凭证与个人信息，持久化于 %APPDATA%\glacc-auto\credentials.json。
/// 仅含用户数据：手机号、设备标识、账号 sub、token。官方常量见 <see cref="GlaccConstants"/>。
/// </summary>
public sealed class GlaccCredentials
{
    /// <summary>手机号（登录用，"+86 12xxxxxxxxx" 格式）</summary>
    public string Phone { get; set; } = "";

    /// <summary>登录域设备 ID（32 位 hex，captcha meta 与 auth 请求头）</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>游戏域设备标识（32 位 hex，peerid / x-device-id / x-guid）</summary>
    public string PeerId { get; set; } = "";

    /// <summary>用户 ID（登录响应 sub）</summary>
    public string Sub { get; set; } = "";

    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    /// <summary>access_token 获取时间（Unix 秒）</summary>
    public long ObtainedAt { get; set; }
    /// <summary>access_token 有效期（秒，默认 7200）</summary>
    public int ExpiresIn { get; set; } = 7200;

    // ── 进行中的短信登录状态（验证码 5 分钟有效，应用重启后可续用）──
    public string VerificationId { get; set; } = "";
    /// <summary>verification_id 签发时间（Unix 秒）</summary>
    public long VerificationIdAt { get; set; }

    /// <summary>设备档案（UA 与 TLS 指纹来源）；登录发码时按手机号哈希分配并持久化</summary>
    public GlaccDeviceProfile? Device { get; set; }

    [JsonIgnore] public bool HasToken => !string.IsNullOrEmpty(AccessToken);
    [JsonIgnore] public bool HasRefreshToken => !string.IsNullOrEmpty(RefreshToken);
    [JsonIgnore] public long JwtExpiresAt => ObtainedAt + ExpiresIn;

    /// <summary>设备档案（未分配时回退池首档案，即真机抓包档案，不持久化）</summary>
    [JsonIgnore] public GlaccDeviceProfile DeviceOrDefault => Device ?? GlaccDevicePool.Profiles[0];

    /// <summary>JWT 是否仍有效（留 60s 余量）</summary>
    [JsonIgnore]
    public bool IsJwtValid => HasToken && NowSeconds() < JwtExpiresAt - 60;

    public static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "glacc-auto", "credentials.json");

    public static GlaccCredentials Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(FilePath), GlaccJsonContext.Default.GlaccCredentials);
                if (loaded is not null)
                {
                    loaded.EnsureDeviceIds();
                    return loaded;
                }
            }
        }
        catch
        {
            // 凭证文件损坏时回退全新凭证
        }
        var fresh = new GlaccCredentials();
        fresh.EnsureDeviceIds();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, GlaccJsonContext.Default.GlaccCredentials));
        }
        catch
        {
            // 保存失败不阻断流程
        }
    }

    /// <summary>首次使用时生成本机随机设备标识（32 位 hex）。</summary>
    public void EnsureDeviceIds()
    {
        if (string.IsNullOrEmpty(DeviceId)) DeviceId = NewHex32();
        if (string.IsNullOrEmpty(PeerId)) PeerId = NewHex32();
    }

    /// <summary>
    /// 确保已分配设备档案：无则按手机号哈希从内置池确定性选档并持久化
    /// （同一手机号永远同一档案；池更新也不影响已分配账号）。
    /// </summary>
    public void EnsureDevice()
    {
        if (Device is not null) return;
        var digits = PhoneDigits(Phone);
        Device = digits.Length == 11
            ? GlaccDevicePool.SelectForPhone(digits)
            : GlaccDevicePool.Profiles[0];
        Save();
    }

    private static string PhoneDigits(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 13 && digits.StartsWith("86")) digits = digits[2..];
        return digits;
    }

    private static string NewHex32()
    {
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexString(buf).ToLowerInvariant();
    }
}
