using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace DevicePanel.Web.Tests;

public class AuthenticationGateTests : IClassFixture<AuthenticationGateTests.Factory>
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    private readonly Factory _factory;

    public AuthenticationGateTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_Root_Redirects_To_Login()
    {
        var client = new WebApplicationFactoryClientOptionsAdapter(_factory).CreateClientWithoutAutoRedirect();
        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Anonymous_Protected_Spa_Route_Redirects_To_Login()
    {
        var client = new WebApplicationFactoryClientOptionsAdapter(_factory).CreateClientWithoutAutoRedirect();
        var response = await client.GetAsync("/devices");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Login_Page_Is_Reachable_Anonymously()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Authenticated_User_Gets_App_Shell_At_Root()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unknown_Api_Endpoint_Returns_401_Not_404_For_Anonymous()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/targets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_Endpoint_Stays_Anonymous()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class WebApplicationFactoryClientOptionsAdapter
    {
        private readonly TestAppFactory _factory;

        public WebApplicationFactoryClientOptionsAdapter(TestAppFactory factory)
        {
            _factory = factory;
        }

        public HttpClient CreateClientWithoutAutoRedirect()
        {
            return _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        }
    }
}
