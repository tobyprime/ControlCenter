using DevicePanel.Web.Auth;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Endpoints;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Metrics;

// wwwroot 双候选解析：发布产物从仓库根目录运行时，静态文件回退到应用目录自带的 wwwroot。
// 解析不到时保持宿主默认探测（如 WebApplicationFactory 场景）；ContentRoot 一律不覆盖。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = WebRootResolver.ResolveWebRoot(Directory.GetCurrentDirectory(), AppContext.BaseDirectory),
});

var databaseOptions = new DatabaseOptions();
builder.Configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);
var configuredDataDir = builder.Configuration["DevicePanel:DataDir"];
if (!string.IsNullOrWhiteSpace(configuredDataDir))
{
    databaseOptions.DataDir = configuredDataDir;
}

if (!Path.IsPathRooted(databaseOptions.DataDir))
{
    databaseOptions.DataDir = Path.GetFullPath(
        Path.Combine(builder.Environment.ContentRootPath, databaseOptions.DataDir));
}

builder.Services.AddSingleton(databaseOptions);
builder.Services.AddSingleton<SqliteConnectionFactory>();

var authOptions = new AuthOptions();
builder.Configuration.GetSection(AuthOptions.SectionName).Bind(authOptions);
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISessionService, SessionService>();
builder.Services.AddSingleton<ILoginRateLimiter, LoginRateLimiter>();

// 设备台账与 agent 接入通道
var agentOptions = new AgentOptions();
builder.Configuration.GetSection(AgentOptions.SectionName).Bind(agentOptions);
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton<IDeviceRegistry, DeviceRegistry>();
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddSingleton<IAgentMessageHandler, HeartbeatMessageHandler>();
builder.Services.AddSingleton<AgentMessageDispatcher>();
builder.Services.AddSingleton<HeartbeatMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatMonitor>());

// 指标采集：入库（明细 + 小时/天聚合）、查询与过期清理
var metricsOptions = new MetricsOptions();
builder.Configuration.GetSection(MetricsOptions.SectionName).Bind(metricsOptions);
builder.Services.AddSingleton(metricsOptions);
builder.Services.AddSingleton<IMetricsStore, MetricsStore>();
builder.Services.AddSingleton<IAgentMessageHandler, MetricsMessageHandler>();
builder.Services.AddSingleton<MetricsRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsRetentionService>());

builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<AccountSeeder>();

var app = builder.Build();

app.UseMiddleware<DevicePanel.Web.Auth.AuthenticationGateMiddleware>();
app.UseStaticFiles();
app.UseWebSockets();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapMetricsEndpoints();
app.MapAgentWsEndpoints();

app.Run();

public partial class Program { }
