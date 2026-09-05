using System.Security.Cryptography;
using System.Text;
using DevicePanel.Web.Infrastructure;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// token 约定锚点测试（TOB-375 审查问题1）：`dpk_` 前缀 + SHA-256 大写十六进制入库是认证链路的安全约定，
/// 迁移平移（013_agents.sql）与存量部署都依赖它——变更前必须显式改这里。
/// </summary>
public class AgentTokenTests
{
    [Fact]
    public void Generate_Follows_Prefix_And_Randomness_Convention()
    {
        var token = AgentToken.Generate();

        Assert.StartsWith("dpk_", token);
        Assert.True(token.Length > "dpk_".Length);
        Assert.NotEqual(AgentToken.Generate(), token); // 随机 32 字节
    }

    [Fact]
    public void Hash_Is_Uppercase_Sha256_Hex_Of_Utf8_Token()
    {
        var token = "dpk_约定的token";

        var hash = AgentToken.Hash(token);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), hash);
        Assert.Equal(64, hash.Length);
    }
}
