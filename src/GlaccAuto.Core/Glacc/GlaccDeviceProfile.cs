using System.Security.Cryptography;
using System.Text;

namespace GlaccAuto.Core.Glacc;

/// <summary>设备档案：UA 与 TLS 指纹的来源。字段必须是现实中存在的组合（机型+OS+Build+WebView 配套）。</summary>
public sealed class GlaccDeviceProfile
{
    /// <summary>登录域 UA 的 deviceName（如 Redmi_2210132C；小米系与机型同名的机型两值相同）</summary>
    public string DeviceName { get; set; } = "";
    /// <summary>设备型号（deviceModel / WebView UA 中的机型段）</summary>
    public string DeviceModel { get; set; } = "";
    /// <summary>Android 版本（决定 TLS 预设 okhttp4_android_{n}）</summary>
    public int OsVersion { get; set; }
    /// <summary>系统 Build 号（WebView UA 的 Build 段）</summary>
    public string OsBuild { get; set; } = "";
    /// <summary>系统 WebView 的 Chrome 版本</summary>
    public string ChromeVersion { get; set; } = "";
    /// <summary>内核版本（登录域 UA 的 Linux 段）</summary>
    public string KernelVersion { get; set; } = "";
}

/// <summary>
/// 内置真实设备档案池（小米/Redmi 系为主，命名与官方 UA 规则一致）。
/// 按手机号哈希确定性选档：同号恒定、异号分散。选档结果不持久化，每次按当前手机号重算。
/// </summary>
public static class GlaccDevicePool
{
    public static readonly IReadOnlyList<GlaccDeviceProfile> Profiles =
    [
        new() { DeviceName = "Redmi_25102Rkbec", DeviceModel = "25102RKBEC", OsVersion = 9,
                OsBuild = "PQ3B.190801.07131748", ChromeVersion = "91.0.4472.114", KernelVersion = "4_4_146" },
        new() { DeviceName = "Redmi_2210132C", DeviceModel = "2210132C", OsVersion = 12,
                OsBuild = "SKQ1.211006.001", ChromeVersion = "107.0.5304.141", KernelVersion = "4_19_157" },
        new() { DeviceName = "Redmi_2210132C", DeviceModel = "2210132C", OsVersion = 13,
                OsBuild = "TKQ1.221114.001", ChromeVersion = "119.0.6045.163", KernelVersion = "5_4_210" },
        new() { DeviceName = "2201123C", DeviceModel = "2201123C", OsVersion = 13,
                OsBuild = "TKQ1.220829.002", ChromeVersion = "119.0.6045.163", KernelVersion = "5_10_101" },
        new() { DeviceName = "Redmi_23013RK75C", DeviceModel = "23013RK75C", OsVersion = 13,
                OsBuild = "TKQ1.220905.001", ChromeVersion = "116.0.5845.163", KernelVersion = "5_10_157" },
        new() { DeviceName = "2304DPN8BC", DeviceModel = "2304DPN8BC", OsVersion = 13,
                OsBuild = "TKQ1.221114.001", ChromeVersion = "119.0.6045.163", KernelVersion = "5_10_157" },
        new() { DeviceName = "Redmi_23127PN0CC", DeviceModel = "23127PN0CC", OsVersion = 14,
                OsBuild = "UKQ1.230917.001", ChromeVersion = "124.0.6367.179", KernelVersion = "5_15_104" },
        new() { DeviceName = "24031PN0DC", DeviceModel = "24031PN0DC", OsVersion = 14,
                OsBuild = "UKQ1.230917.001", ChromeVersion = "124.0.6367.179", KernelVersion = "5_15_94" },
        new() { DeviceName = "Redmi_23054RA19C", DeviceModel = "23054RA19C", OsVersion = 13,
                OsBuild = "TKQ1.221114.001", ChromeVersion = "118.0.5993.65", KernelVersion = "5_10_157" },
        new() { DeviceName = "Redmi_22120RN86C", DeviceModel = "22120RN86C", OsVersion = 13,
                OsBuild = "TKQ1.220905.001", ChromeVersion = "117.0.5938.60", KernelVersion = "5_10_101" },
        new() { DeviceName = "2201117TY", DeviceModel = "2201117TY", OsVersion = 12,
                OsBuild = "SKQ1.211006.001", ChromeVersion = "107.0.5304.141", KernelVersion = "4_19_157" },
        new() { DeviceName = "2112123AG", DeviceModel = "2112123AG", OsVersion = 12,
                OsBuild = "SKQ1.211006.001", ChromeVersion = "103.0.5060.71", KernelVersion = "4_19_157" },
    ];

    /// <summary>按手机号（11 位数字串）确定性选档。</summary>
    public static GlaccDeviceProfile SelectForPhone(string phoneDigits)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("glacc-device:" + phoneDigits));
        var idx = (int)(BitConverter.ToUInt32(hash) % Profiles.Count);
        return Profiles[idx];
    }

    /// <summary>TLS 预设跟随设备 OS 版本（okhttp4_android_7~13），UA 与 TLS 层自洽。</summary>
    public static string IdentifierFor(GlaccDeviceProfile device) =>
        $"okhttp4_android_{Math.Clamp(device.OsVersion, 7, 13)}";
}

/// <summary>官方客户端 UA 模板（拼接格式逐字对照真机抓包），设备段来自设备档案。</summary>
public static class GlaccUa
{
    /// <summary>登录域（user.geilijiasu.net，xbase 账号 SDK）UA。</summary>
    public static string Auth(GlaccDeviceProfile d) =>
        $"ANDROID-com.geilijiasu.glacc/{GlaccConstants.AppVersion} netWorkType/WIFI appid/22070 " +
        $"deviceName/{d.DeviceName} deviceModel/{d.DeviceModel} OSVersion/{d.OsVersion} " +
        "protocolVersion/301 platformVersion/10 sdkVersion/0 Oauth2Client/0.9 " +
        $"(Linux {d.KernelVersion}) (JAVA 0)";

    /// <summary>游戏域（game-xacc.xunlei.com，RN OkHttp + WebView UA 字符串）UA。</summary>
    public static string Game(GlaccDeviceProfile d) =>
        $"xlacc/{GlaccConstants.AppVersion} Mozilla/5.0 (Linux; Android {d.OsVersion}; {d.DeviceModel} " +
        $"Build/{d.OsBuild}; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 " +
        $"Chrome/{d.ChromeVersion} Mobile Safari/537.36";
}
