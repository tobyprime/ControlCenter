using DevicePanel.Web.Auth;
using DevicePanel.Web.Endpoints;
using DevicePanel.Web.Infrastructure;

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

builder.Services.AddHostedService<DatabaseInitializer>();
builder.Services.AddHostedService<AccountSeeder>();

var app = builder.Build();

app.UseMiddleware<DevicePanel.Web.Auth.AuthenticationGateMiddleware>();
app.UseStaticFiles();

app.MapHealthEndpoints();
app.MapAuthEndpoints();

app.Run();

public partial class Program { }
