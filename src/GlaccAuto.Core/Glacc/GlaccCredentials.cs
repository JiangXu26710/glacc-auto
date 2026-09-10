using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlaccAuto.Core.Glacc;

/// <summary>
/// 用户凭证与账号态，持久化于 %APPDATA%\glacc-auto\credentials.json。
/// 落盘内容 = 装机盐（非个人信息）与用户数据（手机号、账号 sub、token）；
/// 设备标识与设备档案由装机盐与手机号派生、不落盘，随手机号清空一并失效。
/// 官方常量见 <see cref="GlaccConstants"/>。
/// </summary>
public sealed class GlaccCredentials
{
    /// <summary>手机号（登录用，"12xxxxxxxxx" 格式）</summary>
    public string Phone { get; set; } = "";

    /// <summary>
    /// 进行中短信登录的目标手机号（与 <see cref="Phone"/> 同格式）。
    /// 发送验证码时写入，登录成功转正为 <see cref="Phone"/>；登录流程之外不改动 <see cref="Phone"/>，
    /// 改号登录期间已登录会话的显示与游戏域设备身份保持不变。
    /// 与 VerificationId 一样落盘，应用重启后可续用。
    /// </summary>
    public string PendingPhone { get; set; } = "";

    /// <summary>
    /// 装机盐（32 位 hex）：首次运行时随机生成并持久化，仅参与设备标识派生，不含任何用户信息。
    /// 退出登录不重置，使同一手机号在本机重登时复现同一套设备标识。
    /// </summary>
    public string InstallSalt { get; set; } = "";

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

    [JsonIgnore] public bool HasToken => !string.IsNullOrEmpty(AccessToken);
    [JsonIgnore] public bool HasRefreshToken => !string.IsNullOrEmpty(RefreshToken);
    [JsonIgnore] public long JwtExpiresAt => ObtainedAt + ExpiresIn;

    /// <summary>当前登录流程的目标手机号：进行中的改号登录取 <see cref="PendingPhone"/>，否则取已登录的 <see cref="Phone"/>。</summary>
    [JsonIgnore] public string LoginPhone => PendingPhone.Length > 0 ? PendingPhone : Phone;

    /// <summary>
    /// 登录域设备 ID（32 位 hex，captcha meta 与 auth 请求头）。
    /// 由装机盐与手机号派生：同机同号恒定、换号或换机必不同，且无法由手机号反推。
    /// </summary>
    [JsonIgnore] public string DeviceId => DeriveHex("glacc-deviceid:");

    /// <summary>登录域设备 ID（短信登录流程用）：跟随 <see cref="LoginPhone"/> 派生，派生规则同 <see cref="DeviceId"/>。</summary>
    [JsonIgnore] public string LoginDeviceId => DeriveHex("glacc-deviceid:", PhoneDigits(LoginPhone));

    /// <summary>游戏域设备标识（32 位 hex，peerid / x-device-id / x-guid），派生规则同 <see cref="DeviceId"/>。</summary>
    [JsonIgnore] public string PeerId => DeriveHex("glacc-peerid:");

    /// <summary>设备档案（UA 与 TLS 指纹来源）：按手机号哈希从内置池确定性选档，同号恒定、异号分散。</summary>
    [JsonIgnore]
    public GlaccDeviceProfile Device => DeviceFor(PhoneDigits(Phone));

    /// <summary>登录域设备档案（短信登录流程用）：跟随 <see cref="LoginPhone"/> 选档，规则同 <see cref="Device"/>。</summary>
    [JsonIgnore]
    public GlaccDeviceProfile LoginDevice => DeviceFor(PhoneDigits(LoginPhone));

    private static GlaccDeviceProfile DeviceFor(string digits) =>
        digits.Length == 11 ? GlaccDevicePool.SelectForPhone(digits) : GlaccDevicePool.Profiles[0];

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
                    loaded.EnsureInstallSalt();
                    return loaded;
                }
            }
        }
        catch
        {
            // 凭证文件损坏时回退全新凭证
        }
        var fresh = new GlaccCredentials();
        fresh.EnsureInstallSalt();
        return fresh;
    }

    private static readonly object SaveGate = new();

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, GlaccJsonContext.Default.GlaccCredentials);
            lock (SaveGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // 临时文件 + 原子替换：多线程并发保存或进程中途退出都不会留下写了一半的凭证文件
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, true);
            }
        }
        catch
        {
            // 保存失败不阻断流程
        }
    }

    /// <summary>
    /// 退出登录：清空手机号、账号 ID、令牌与进行中的短信登录。
    /// 设备标识与设备档案由装机盐与手机号派生、不落盘，随手机号清空一并失效。
    /// 不含持久化，调用方按需再存盘。
    /// </summary>
    public void ClearSession()
    {
        Phone = "";
        PendingPhone = "";
        Sub = "";
        AccessToken = "";
        RefreshToken = "";
        ObtainedAt = 0;
        ExpiresIn = 7200;
        VerificationId = "";
        VerificationIdAt = 0;
    }

    /// <summary>首次使用时生成装机盐（32 位 hex）并落盘。</summary>
    public void EnsureInstallSalt()
    {
        if (!string.IsNullOrEmpty(InstallSalt)) return;
        InstallSalt = NewHex32();
        Save();
    }

    /// <summary>
    /// 派生 32 位 hex 设备标识：材料 = 装机盐 + 手机号（尚无有效手机号时只用装机盐，
    /// 保证标识始终非空且同机恒定）；域前缀使 DeviceId 与 PeerId 互相独立。
    /// </summary>
    private string DeriveHex(string domain) => DeriveHex(domain, PhoneDigits(Phone));

    private string DeriveHex(string domain, string digits)
    {
        var material = digits.Length == 11 ? $"{InstallSalt}:{digits}" : InstallSalt;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(domain + material));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
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
