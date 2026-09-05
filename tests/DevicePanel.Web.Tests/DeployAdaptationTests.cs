using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Xunit;

namespace DevicePanel.Web.Tests;

/// <summary>
/// 部署适配（TOB-357 真实环境）：跨域前端（Cloudflare Pages）所需的
/// 会话 Cookie SameSite/Secure 可配置与 CORS 允许来源配置。
/// </summary>
public class DeployAdaptationTests
{
    // ---- 会话 Cookie SameSite 可配置 ----

    [Fact]
    public async Task Login_Default_Cookie_Is_Lax_Without_Secure()
    {
        var factory = new DeployFactory();
        var client = factory.CreateClient();

        var response = await LoginAsync(client);
        var cookie = GetSetCookie(response);

        Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("secure", cookie);
    }

    [Fact]
    public async Task Login_SameSite_None_Sets_Secure_Cookie_And_Logout_Deletes_With_Same_Attributes()
    {
        var factory = new DeployFactory();
        factory.Settings["DevicePanel:Auth:SessionCookieSameSite"] = "None";
        var client = factory.CreateClient();

        var login = GetSetCookie(await LoginAsync(client));
        Assert.Contains("samesite=none", login);
        Assert.Contains("secure", login);

        var logout = GetSetCookie(await client.PostAsync("/api/auth/logout", null));
        Assert.Contains("samesite=none", logout);
        Assert.Contains("secure", logout);
    }

    [Fact]
    public async Task Login_SameSite_Strict_Is_Accepted()
    {
        var factory = new DeployFactory();
        factory.Settings["DevicePanel:Auth:SessionCookieSameSite"] = "Strict";
        var client = factory.CreateClient();

        var cookie = GetSetCookie(await LoginAsync(client));
        Assert.Contains("samesite=strict", cookie);
    }

    [Fact]
    public async Task Login_Invalid_SameSite_Fails_Fast_With_Clear_Message()
    {
        var factory = new DeployFactory();
        factory.Settings["DevicePanel:Auth:SessionCookieSameSite"] = "whatever";

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { username = "admin", password = "test-password-1" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("SessionCookieSameSite", await response.Content.ReadAsStringAsync());
    }

    // ---- CORS 允许来源可配置 ----

    [Fact]
    public async Task Api_Without_Cors_Config_Returns_No_Cors_Headers()
    {
        var factory = new DeployFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/collectors");
        request.Headers.Add("Origin", "https://panel.example.com");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_Without_Cors_Config_Is_Rejected_As_Unauthorized()
    {
        var factory = new DeployFactory();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/collectors");
        request.Headers.Add("Origin", "https://panel.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_With_Allowed_Origin_Gets_Credential_Cors_Headers()
    {
        var factory = new DeployFactory();
        factory.Settings["DevicePanel:Cors:AllowedOrigins"] = "https://panel.example.com;https://cc.pages.dev";
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/collectors");
        request.Headers.Add("Origin", "https://panel.example.com");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("https://panel.example.com", GetHeader(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", GetHeader(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Api_Get_With_Allowed_Origin_Echoes_Origin()
    {
        var factory = new DeployFactory();
        factory.Settings["DevicePanel:Cors:AllowedOrigins"] = "https://panel.example.com";
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/collectors");
        request.Headers.Add("Origin", "https://panel.example.com");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("https://panel.example.com", GetHeader(response, "Access-Control-Allow-Origin"));
        Assert.Equal("true", GetHeader(response, "Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Api_Get_With_Disallowed_Origin_Gets_No_Cors_Headers()
    {
        var factory = new DeployFactory();
        factory.Settings["DevicePanel:Cors:AllowedOrigins"] = "https://panel.example.com";
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/collectors");
        request.Headers.Add("Origin", "https://evil.example.org");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Api_Get_With_Blank_Origin_Entries_Ignored()
    {
        var factory = new DeployFactory();
        factory.Settings["DevicePanel:Cors:AllowedOrigins"] = " , https://panel.example.com ,,";
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/collectors");
        request.Headers.Add("Origin", "https://panel.example.com");

        var response = await client.SendAsync(request);

        Assert.Equal("https://panel.example.com", GetHeader(response, "Access-Control-Allow-Origin"));
    }

    private sealed class DeployFactory : TestAppFactory
    {
        public DeployFactory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client)
    {
        return await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
    }

    private static string GetSetCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault()
            : null;
        Assert.NotNull(setCookie);
        return setCookie!.ToLowerInvariant();
    }

    private static string GetHeader(HttpResponseMessage response, string name)
    {
        var values = response.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
        Assert.NotNull(values);
        return values!;
    }
}
