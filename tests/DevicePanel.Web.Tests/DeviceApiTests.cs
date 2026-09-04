using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DevicePanel.Web.Tests;

public class DeviceApiTests : IDisposable
{
    public sealed class Factory : TestAppFactory
    {
        public Factory()
        {
            Settings["DevicePanel:Auth:InitialPassword"] = "test-password-1";
        }
    }

    // 每个测试独立 Factory：设备数据互不干扰
    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Devices_Require_Login()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/devices");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_Still_Succeeds_And_Logs_Warning_When_Rule_Seeder_Fails()
    {
        // 默认告警规则种子失败：设备创建不受阻（台账先行），但必须有 Warning 日志便于发现补配
        var logs = new List<string>();
        var factory = new Factory();
        factory.TestServices = services =>
        {
            services.RemoveAll<Alerting.AlertRuleSeeder>();
            services.AddSingleton<Alerting.AlertRuleSeeder>(new ThrowingRuleSeeder());
            services.AddSingleton<ILoggerProvider>(new CollectingLoggerProvider(logs));
        };
        try
        {
            var client = factory.CreateClient();
            await LoginAsync(client);

            var create = await client.PostAsJsonAsync("/api/devices", new { name = "种子故障设备", tags = new[] { "告警" } });
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            Assert.Contains(logs, line => line.Contains("Warning") && line.Contains("种子故障设备"));
        }
        finally
        {
            factory.Dispose();
        }
    }

    private static async Task LoginAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        login.EnsureSuccessStatusCode();
    }

    private sealed class ThrowingRuleSeeder : Alerting.AlertRuleSeeder
    {
        public ThrowingRuleSeeder() : base(null!, null!, null!, new Alerting.AlertOptions(), new Devices.AgentOptions())
        {
        }

        public override Targets.TargetInfo EnsureForDevice(long deviceId, string deviceName, bool useEffectiveThresholds = false) =>
            throw new InvalidOperationException("注入的种子故障");
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _logs;

        public CollectingLoggerProvider(List<string> logs) => _logs = logs;

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(_logs);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger : ILogger
        {
            private readonly List<string> _logs;

            public CollectingLogger(List<string> logs) => _logs = logs;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _logs.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }

    [Fact]
    public async Task Create_Returns_Token_Once_And_List_Shows_Device_Offline()
    {
        var client = await AuthenticatedClientAsync();

        var create = await client.PostAsJsonAsync("/api/devices", new { name = "办公区打印机主机", tags = new[] { "办公区", "打印" } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var token = created.GetProperty("agentToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(created.GetProperty("id").GetInt64() > 0);

        var list = await ListAsync(client);
        var device = Assert.Single(list);
        Assert.Equal("办公区打印机主机", device.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Array, device.GetProperty("tags").ValueKind);
        Assert.False(device.GetProperty("online").GetBoolean());
        Assert.Null(device.GetProperty("lastSeenAtUtc").GetString());

        // agent token 只在创建/重置响应中出现，列表不回显
        Assert.False(device.TryGetProperty("agentToken", out _));
    }

    [Fact]
    public async Task Create_With_Blank_Name_Returns_400()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/devices", new { name = "   ", tags = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_With_Too_Many_Tags_Returns_400()
    {
        var client = await AuthenticatedClientAsync();
        var tags = Enumerable.Range(1, 21).Select(i => $"标签{i}").ToArray();

        var response = await client.PostAsJsonAsync("/api/devices", new { name = "设备", tags });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_Changes_Name_And_Tags()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client, "旧名", new[] { "旧" });

        var update = await client.PutAsJsonAsync($"/api/devices/{created.Id}", new { name = "新名", tags = new[] { "位置A", "用途B" } });

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var list = await ListAsync(client);
        var device = Assert.Single(list);
        Assert.Equal("新名", device.GetProperty("name").GetString());
        Assert.Equal(2, device.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task Update_Missing_Device_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PutAsJsonAsync("/api/devices/987654", new { name = "任意", tags = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Removes_Device_From_List()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client, "待删除设备", Array.Empty<string>());

        var delete = await client.DeleteAsync($"/api/devices/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        Assert.Empty(await ListAsync(client));

        var again = await client.DeleteAsync($"/api/devices/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task ResetToken_Returns_New_Token_Different_From_Old()
    {
        var client = await AuthenticatedClientAsync();
        var created = await CreateDeviceAsync(client, "设备", Array.Empty<string>());

        var reset = await client.PostAsJsonAsync($"/api/devices/{created.Id}/token", new { });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var payload = await reset.Content.ReadFromJsonAsync<JsonElement>();
        var newToken = payload.GetProperty("agentToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(newToken));
        Assert.NotEqual(created.AgentToken, newToken);
    }

    [Fact]
    public async Task ResetToken_Missing_Device_Returns_404()
    {
        var client = await AuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/devices/555555/token", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<HttpClient> AuthenticatedClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "test-password-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<(long Id, string AgentToken)> CreateDeviceAsync(HttpClient client, string name, string[] tags)
    {
        var response = await client.PostAsJsonAsync("/api/devices", new { name, tags });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (payload.GetProperty("id").GetInt64(), payload.GetProperty("agentToken").GetString()!);
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/devices");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        return list.EnumerateArray().ToArray();
    }
}
