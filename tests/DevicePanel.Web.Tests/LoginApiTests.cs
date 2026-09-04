using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace DevicePanel.Web.Tests;

public class LoginApiTests : IClassFixture<LoginApiTests.Factory>
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    private readonly Factory _factory;

    public LoginApiTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_With_Correct_Password_Returns_Ok_And_Sets_Session_Cookie()
    {
        var client = _factory.CreateClient();
        var response = await Login(client, "admin", "test-password-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers, h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Login_With_Wrong_Password_Returns_401()
    {
        var client = _factory.CreateClient();
        var response = await Login(client, "admin", "wrong-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_With_Unknown_User_Returns_401()
    {
        var client = _factory.CreateClient();
        var response = await Login(client, "ghost", "whatever");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_Without_Cookie_Returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_With_Session_Cookie_Returns_Username()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "test-password-1");

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("admin", payload.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Logout_Invalidates_Session_Immediately()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "test-password-1");

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string username, string password)
    {
        return await client.PostAsJsonAsync("/api/auth/login", new { username, password });
    }
}

public class LoginRateLimitApiTests : IClassFixture<LoginRateLimitApiTests.Factory>
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
            Settings["DevicePanel:Auth:MaxFailedAttempts"] = "2";
            Settings["DevicePanel:Auth:LockoutSeconds"] = "1";
        }
    }

    private readonly Factory _factory;

    public LoginRateLimitApiTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Reaching_Threshold_Locks_Even_With_Correct_Password()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "wrong-1");
        await Login(client, "admin", "wrong-2");

        var locked = await Login(client, "admin", "test-password-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    [Fact]
    public async Task After_Lockout_Window_Correct_Password_Succeeds()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "wrong-1");
        await Login(client, "admin", "wrong-2");
        var locked = await Login(client, "admin", "test-password-1");
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);

        await Task.Delay(1200);
        var retry = await Login(client, "admin", "test-password-1");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task Successful_Login_Resets_Failure_Counter()
    {
        var client = _factory.CreateClient();
        await Login(client, "admin", "wrong-1");
        await Login(client, "admin", "test-password-1");
        await Login(client, "admin", "wrong-2");
        var retry = await Login(client, "admin", "test-password-1");

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string username, string password)
    {
        return await client.PostAsJsonAsync("/api/auth/login", new { username, password });
    }
}
