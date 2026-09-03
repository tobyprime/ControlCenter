using DevicePanel.Web.Endpoints;
using DevicePanel.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddHostedService<DatabaseInitializer>();

var app = builder.Build();

app.MapHealthEndpoints();

app.Run();

public partial class Program { }
