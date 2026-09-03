using DevicePanel.Web.Alerting;
using DevicePanel.Web.Auth;
using DevicePanel.Web.Devices;
using DevicePanel.Web.Endpoints;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Logs;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Terminal;

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

// 告警分发：规则（离线 / 阈值越限）→ 渠道抽象（QQ/napcat 首实现）→ 本地待发队列断线补发（无丢失）
var alertOptions = new AlertOptions();
builder.Configuration.GetSection(AlertOptions.SectionName).Bind(alertOptions);
builder.Services.AddSingleton(alertOptions);
builder.Services.AddSingleton<IAlertOutboxStore, AlertOutboxStore>();
builder.Services.AddSingleton<IAlertSettingsStore, AlertSettingsStore>();
builder.Services.AddSingleton<IAlertThresholdStore, AlertThresholdStore>();
builder.Services.AddSingleton<IAlertStateStore, AlertStateStore>();
builder.Services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
builder.Services.AddSingleton<INotifier, NapcatNotifier>();
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddSingleton<IThresholdAlertEvaluator, ThresholdAlertEvaluator>();
builder.Services.AddSingleton<AlertDispatchWorker>();
builder.Services.AddSingleton<OfflineAlertScanner>();

// Web 终端：浏览器 ↔ agent 中继、留痕存储与 term.* 下行处理
builder.Services.AddSingleton<ITerminalStore, TerminalStore>();
builder.Services.AddSingleton<TerminalSessionRegistry>();
builder.Services.AddSingleton<IAgentMessageHandler, TermOpenedHandler>();
builder.Services.AddSingleton<IAgentMessageHandler, TermOutputHandler>();
builder.Services.AddSingleton<IAgentMessageHandler, TermClosedHandler>();
builder.Services.AddSingleton<IAgentMessageHandler, TermErrorHandler>();

// 日志查看：按需只读拉取（logs.* 请求-响应），不落库
var logsOptions = new LogsOptions();
builder.Configuration.GetSection(LogsOptions.SectionName).Bind(logsOptions);
builder.Services.AddSingleton(logsOptions);
builder.Services.AddSingleton<LogQueryService>();
builder.Services.AddSingleton<IAgentMessageHandler, LogsServicesResponseHandler>();
builder.Services.AddSingleton<IAgentMessageHandler, LogsTailResponseHandler>();
builder.Services.AddSingleton<IAgentMessageHandler, LogsErrorHandler>();

builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<AccountSeeder>();

// 告警分发 worker 依赖迁移完成后的表结构：必须排在 DatabaseInitializer 之后启动
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertDispatchWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<OfflineAlertScanner>());

// 清理任务依赖迁移完成后的表结构：必须排在 DatabaseInitializer 之后启动
builder.Services.AddSingleton<MetricsRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsRetentionService>());

var app = builder.Build();

app.UseMiddleware<DevicePanel.Web.Auth.AuthenticationGateMiddleware>();
app.UseStaticFiles();
app.UseWebSockets();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapDeviceEndpoints();
app.MapMetricsEndpoints();
app.MapAlertEndpoints();
app.MapTerminalEndpoints();
app.MapLogEndpoints();
app.MapAgentWsEndpoints();

app.Run();

public partial class Program { }
