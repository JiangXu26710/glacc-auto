using System.Security.Cryptography;
using System.Text;

namespace GlaccAuto.Core.Glacc;

/// <summary>mobileGLTaskPush 请求签名（MD5）。盐作为名为 key 的参数参与字典序排序，extData 不参与签名。</summary>
public static class GlaccSign
{
    /// <summary>
    /// sign = md5("key={盐}&amp;masterTaskId={m}&amp;taskId={t}&amp;timestamp={ts}")
    /// 拼接按 key 字典序升序：key &lt; masterTaskId &lt; taskId &lt; timestamp，'&amp;' 连接，32 位小写。
    /// </summary>
    public static string PushSign(int masterTaskId, int taskId, long timestamp)
    {
        var s = $"key={GlaccConstants.SignKey}&masterTaskId={masterTaskId}" +
                $"&taskId={taskId}&timestamp={timestamp}";
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }

    /// <summary>已知抓包样本自校验（masterTaskId=78, taskId=178, ts=1786116529）。</summary>
    public static bool SelfTest() =>
        PushSign(78, 178, 1786116529) == "7e63fa2e335360557eeb0b0507c3691d";
}
