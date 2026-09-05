using DevicePanel.Web.Alerting;
using DevicePanel.Web.Auth;
using DevicePanel.Web.Dashboard;
using DevicePanel.Web.Endpoints;
using DevicePanel.Web.Agents;
using DevicePanel.Web.Control;
using DevicePanel.Web.Infrastructure;
using DevicePanel.Web.Interactions;
using DevicePanel.Web.Logs;
using DevicePanel.Web.Metrics;
using DevicePanel.Web.Probing;
using DevicePanel.Web.Collectors;
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

// 跨域前端（如 Cloudflare Pages 独立域名）：配置了允许来源才启用 CORS（凭据模式，回显具体来源）
var corsSettings = new CorsSettings();
builder.Configuration.GetSection(CorsSettings.SectionName).Bind(corsSettings);
var allowedOrigins = corsSettings.ResolvedAllowedOrigins();
if (allowedOrigins.Count > 0)
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins.ToArray())
            .AllowCredentials()
            .SetIsOriginAllowedToAllowWildcardSubdomains()
            .AllowAnyHeader()
            .AllowAnyMethod()));
}

// 目标台账（设备/服务统一实体）与 agent 实体（三期模块2：一 agent 一 token，token 唯一宿主）、接入通道
var agentOptions = new AgentOptions();
builder.Configuration.GetSection(AgentOptions.SectionName).Bind(agentOptions);
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton<IAgentRegistry, AgentRegistry>();
builder.Services.AddSingleton<ICollectorRegistry, CollectorRegistry>();
builder.Services.AddSingleton<AgentConnectionRegistry>();
builder.Services.AddSingleton<IAgentMessageHandler, HeartbeatMessageHandler>();
builder.Services.AddSingleton<IAgentMessageHandler, AgentCapabilitiesMessageHandler>();
builder.Services.AddSingleton<AgentMessageDispatcher>();
builder.Services.AddSingleton<HeartbeatMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatMonitor>());

// 指标语义中立管道（约束 A）：MetricKey 注册表 + KV 序列存储（明细 + 小时/天聚合）、查询与过期清理
var metricsOptions = new MetricsOptions();
builder.Configuration.GetSection(MetricsOptions.SectionName).Bind(metricsOptions);
builder.Services.AddSingleton(metricsOptions);
builder.Services.AddSingleton<IMetricKeyRegistry, MetricKeyRegistry>();
builder.Services.AddSingleton<IMetricsStore, MetricsStore>();
builder.Services.AddSingleton<IAgentMessageHandler, MetricsMessageHandler>();
// 按需查询（三期模块3）：最新值经 metrics.latest.request 下行 agent 即时采样（pull 采集器直读存储），与日志同一请求-响应模式
builder.Services.AddSingleton<MetricsQueryService>();
builder.Services.AddSingleton<IAgentMessageHandler, MetricsLatestResponseHandler>();
builder.Services.AddSingleton<IAgentMessageHandler, MetricsQueryErrorHandler>();
// 采集器数据类型注册表（验收8）：新增数据类型 = 注册一个 ICollectorDataType 实现
builder.Services.AddSingleton<ICollectorDataType, MetricsDataType>();
builder.Services.AddSingleton<ICollectorDataType, LogsDataType>();
builder.Services.AddSingleton<CollectorDataTypeCatalog>();

// 告警规则化（约束 B）：规则实例（可插拔规则类型）→ 评估引擎 → 渠道抽象 → 本地待发队列断线补发（无丢失）
var alertOptions = new AlertOptions();
builder.Configuration.GetSection(AlertOptions.SectionName).Bind(alertOptions);
builder.Services.AddSingleton(alertOptions);
var napcatSeedOptions = new NapcatSeedOptions();
builder.Configuration.GetSection(NapcatSeedOptions.SectionName).Bind(napcatSeedOptions);
builder.Services.AddSingleton(napcatSeedOptions);
builder.Services.AddSingleton<IAlertOutboxStore, AlertOutboxStore>();
builder.Services.AddSingleton<IAlertSettingsStore, AlertSettingsStore>();
builder.Services.AddSingleton<IAlertStateStore, AlertStateStore>();
builder.Services.AddSingleton<IAlertRuleStore, AlertRuleStore>();
builder.Services.AddSingleton<IAlertRuleType, ThresholdAboveRuleType>();
builder.Services.AddSingleton<IAlertRuleType, ThresholdBelowRuleType>();
builder.Services.AddSingleton<IAlertRuleType, NoDataRuleType>();
builder.Services.AddSingleton<IAlertRuleType, StateMismatchRuleType>();
builder.Services.AddSingleton<AlertRuleEngine>();
builder.Services.AddSingleton<IAlertRuleEngine>(sp => sp.GetRequiredService<AlertRuleEngine>());
builder.Services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
builder.Services.AddSingleton<INotifier, NapcatNotifier>();
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddSingleton<AlertDispatchWorker>();
builder.Services.AddSingleton<CollectorStatusScanner>();
builder.Services.AddSingleton<AlertRuleSweepService>();

// 服务监测（模块2）：面板侧 HTTP/JSON 探针——样本照走指标管道与告警引擎（约束 A/B），不改 agent
var probeOptions = new ProbeOptions();
builder.Configuration.GetSection(ProbeOptions.SectionName).Bind(probeOptions);
builder.Services.AddSingleton(probeOptions);
builder.Services.AddSingleton<IPullCollectorConfigStore, PullCollectorConfigStore>();
builder.Services.AddSingleton<IProbeHttpClient, HttpClientProbeClient>();
builder.Services.AddSingleton<PullCollectorWorker>();

// 主页布局持久化：单用户单套布局存储与读写 API（TOB-366）
builder.Services.AddSingleton<IDashboardLayoutStore, DashboardLayoutStore>();

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

// 控制器统一抽象（三期模块4）：类型注册表（新增类型 = 注册 IControlType）+ 下发（ctrl.* 请求-响应/seq 关联）+ 全量留痕
var controlOptions = new ControlOptions();
builder.Configuration.GetSection(ControlOptions.SectionName).Bind(controlOptions);
builder.Services.AddSingleton(controlOptions);
builder.Services.AddSingleton<IControlType, ButtonControlType>();
builder.Services.AddSingleton<IControlType, ToggleControlType>();
builder.Services.AddSingleton<IControlType, InputControlType>();
builder.Services.AddSingleton<IControlType, SliderControlType>();
builder.Services.AddSingleton<ControlTypeCatalog>();
builder.Services.AddSingleton<IControlLogStore, ControlLogStore>();
builder.Services.AddSingleton<ControlInvokeService>();
builder.Services.AddSingleton<IAgentMessageHandler, ControlInvokeResponseHandler>();
builder.Services.AddSingleton<IAgentMessageHandler, ControlErrorHandler>();

// 交互模式（约束 C）：注册表收集全部 IInteractionMode，核心按目标声明渲染入口，不绑定单一形态
builder.Services.AddSingleton<IInteractionMode, ShellInteractionMode>();
builder.Services.AddSingleton<InteractionModeRegistry>();
builder.Services.AddSingleton<IInteractionModeCatalog, DeviceInteractionModeCatalog>();

builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<AccountSeeder>();

// napcat 配置种子依赖迁移后的表结构：必须在 DatabaseInitializer 之后、分发 worker 之前执行
builder.Services.AddHostedService<AlertSettingsSeeder>();

// 告警分发 worker 依赖迁移完成后的表结构：必须排在 DatabaseInitializer 之后启动
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertDispatchWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<CollectorStatusScanner>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertRuleSweepService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<PullCollectorWorker>());

// 清理任务依赖迁移完成后的表结构：必须排在 DatabaseInitializer 之后启动
builder.Services.AddSingleton<MetricsRetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsRetentionService>());

var app = builder.Build();

// CORS 必须先于登录门禁：预检 OPTIONS 请求不该被 /api 未登录拦截
if (allowedOrigins.Count > 0)
{
    app.UseCors();
}

app.UseMiddleware<DevicePanel.Web.Auth.AuthenticationGateMiddleware>();
app.UseStaticFiles();
app.UseWebSockets();

app.MapHealthEndpoints();
app.MapAuthEndpoints();
app.MapAgentEndpoints();
app.MapCollectorEndpoints();
app.MapPullCollectorEndpoints();
app.MapMetricsEndpoints();
app.MapMetricsLatestEndpoints();
app.MapAlertEndpoints();
app.MapDashboardEndpoints();
app.MapTerminalEndpoints();
app.MapLogEndpoints();
app.MapControlEndpoints();
app.MapInteractionEndpoints();
app.MapAgentWsEndpoints();

app.Run();

public partial class Program { }
