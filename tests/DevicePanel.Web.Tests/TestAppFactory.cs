using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace DevicePanel.Web.Tests;

public class TestAppFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "device-panel-tests", Guid.NewGuid().ToString("N"));

    public IDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("DevicePanel:DataDir", DataDir);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(DataDir))
            {
                Directory.Delete(DataDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort cleanup; WAL sidecar files may still be memory-mapped
        }
    }
}
