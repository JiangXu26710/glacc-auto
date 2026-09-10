using System.Reflection;

namespace GlaccAuto.Gui;

/// <summary>应用信息：版本号取自程序集，主页地址为项目常量</summary>
public static class AppInfo
{
    /// <summary>应用版本号（程序集信息版本，去掉源码修订号后缀）</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>关于行的版本文案</summary>
    public static string VersionText => $"glacc-auto · v{Version}";

    /// <summary>项目主页地址</summary>
    public const string HomepageUrl = "https://github.com/JiangXu26710/glacc-auto";

    /// <summary>项目主页地址，供链接控件跳转</summary>
    public static readonly Uri HomepageUri = new(HomepageUrl);

    /// <summary>Server酱登录地址（登录后可取得 SendKey）</summary>
    public const string ServerChanLoginUrl = "https://sct.ftqq.com/login";

    /// <summary>Server酱登录地址，供链接控件跳转</summary>
    public static readonly Uri ServerChanLoginUri = new(ServerChanLoginUrl);

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "0.0.0";
        }
        // 构建时版本号后附带 "+<源码修订号>"，界面只显示版本号本身
        var revision = informational.IndexOf('+');
        return revision >= 0 ? informational[..revision] : informational;
    }
}
