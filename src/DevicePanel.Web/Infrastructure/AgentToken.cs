using System.Security.Cryptography;
using System.Text;

namespace DevicePanel.Web.Infrastructure;

/// <summary>
/// agent token 约定的唯一实现（三期模块2）：`dpk_` 前缀 + 32 字节随机数 base64，库中仅存 SHA-256 大写十六进制。
/// 迁移平移（013_agents.sql）与存量部署都依赖该约定——签发/认证（AgentRegistry）与镜像/占位（TargetRegistry）共用本类，禁止各持私有拷贝。
/// </summary>
public static class AgentToken
{
    public const string Prefix = "dpk_";

    public static string Generate() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes);
    }
}
