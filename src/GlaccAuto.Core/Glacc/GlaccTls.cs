using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlaccAuto.Core.Glacc;

/// <summary>
/// tls-client 原生库（bogdanfinn/tls-client cffi）最小 P/Invoke 封装，AOT 安全。
/// 全部业务 HTTP 经此栈发出（OkHttp/Android 指纹），避免暴露 .NET SChannel TLS 指纹。
/// 原生 DLL 由 TlsClient.Native.win-x64 NuGet 包随发布分发。
/// 导出为无会话模式：每个请求载荷自带完整配置，返回响应 JSON（含 id），用完 freeMemory(id)。
/// </summary>
public static class GlaccTls
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static RequestDelegate _request = null!;
    private static FreeMemoryDelegate _freeMemory = null!;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RequestDelegate(byte[] payload);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeMemoryDelegate(string id);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string path);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string name);

    /// <summary>定位并加载原生库；失败抛出明确异常（绝不静默降级回 .NET 原生栈）。</summary>
    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            string[] probes =
            [
                Path.Combine(AppContext.BaseDirectory, "tls-client.dll"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", "tls-client", "win", "x64", "tls-client.dll"),
            ];
            var dllPath = probes.FirstOrDefault(File.Exists);
            if (dllPath is null)
                throw new DllNotFoundException(
                    "未找到 TLS 指纹库 tls-client.dll，请重新安装应用（勿删除 runtimes 目录）。");
            var module = LoadLibraryW(dllPath);
            if (module == IntPtr.Zero)
                throw new DllNotFoundException(
                    $"加载 TLS 指纹库失败：{dllPath}（Win32Error={Marshal.GetLastWin32Error()}）");
            _request = GetDelegate<RequestDelegate>(module, "request");
            _freeMemory = GetDelegate<FreeMemoryDelegate>(module, "freeMemory");
            _initialized = true;
        }
    }

    private static T GetDelegate<T>(IntPtr module, string name) where T : Delegate
    {
        var ptr = GetProcAddress(module, name);
        return ptr == IntPtr.Zero
            ? throw new EntryPointNotFoundException($"TLS 指纹库缺少导出函数：{name}")
            : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    /// <summary>
    /// 执行一次 HTTP 请求（阻塞调用，调用方须放在后台线程）。失败返回 null 并给出 error。
    /// </summary>
    public static GlaccTlsResponse? Send(TlsRequestPayload payload, out string error)
    {
        error = "";
        try
        {
            Initialize();
            var json = JsonSerializer.Serialize(payload, GlaccJsonContext.Default.TlsRequestPayload);
            // Go 侧按 C 字符串读取，补 NUL 终止符
            var bytes = new byte[Encoding.UTF8.GetByteCount(json) + 1];
            Encoding.UTF8.GetBytes(json, bytes);
            var ptr = _request(bytes);
            if (ptr == IntPtr.Zero)
            {
                error = "TLS 库返回空指针";
                return null;
            }
            var respJson = Marshal.PtrToStringUTF8(ptr) ?? "";
            if (respJson.Length == 0)
            {
                error = "TLS 库返回空响应";
                return null;
            }
            using var doc = JsonDocument.Parse(respJson);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (!string.IsNullOrEmpty(id))
            {
                try { _freeMemory(id); } catch { /* 释放失败不影响结果 */ }
            }
            return new GlaccTlsResponse(status, body);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}

/// <summary>原生库响应（仅业务需要的字段）。</summary>
public sealed record GlaccTlsResponse(int Status, string Body);

/// <summary>tls-client cffi 请求载荷（字段名与 Go 侧 RequestInput JSON tag 一一对应）。</summary>
public record TlsRequestPayload(
    [property: JsonPropertyName("tlsClientIdentifier")] string TlsClientIdentifier,
    [property: JsonPropertyName("requestMethod")] string RequestMethod,
    [property: JsonPropertyName("requestUrl")] string RequestUrl,
    [property: JsonPropertyName("requestBody")] string? RequestBody,
    [property: JsonPropertyName("headers")] Dictionary<string, string> Headers,
    [property: JsonPropertyName("headerOrder")] List<string>? HeaderOrder,
    [property: JsonPropertyName("timeoutSeconds")] int TimeoutSeconds,
    [property: JsonPropertyName("followRedirects")] bool FollowRedirects,
    [property: JsonPropertyName("withoutCookieJar")] bool WithoutCookieJar,
    [property: JsonPropertyName("isByteRequest")] bool IsByteRequest,
    [property: JsonPropertyName("isByteResponse")] bool IsByteResponse,
    [property: JsonPropertyName("catchPanics")] bool CatchPanics);
