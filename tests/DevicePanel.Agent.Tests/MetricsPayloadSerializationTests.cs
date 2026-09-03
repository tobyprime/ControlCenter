using System.Text.Json;
using DevicePanel.Agent;
using Xunit;

namespace DevicePanel.Agent.Tests;

public class MetricsPayloadSerializationTests
{
    [Fact]
    public void MetricsPayload_Serializes_To_CamelCase_Fields()
    {
        var payload = new MetricsPayload(12.5, 43.2, 61.8, 20480.0, 4096.0);

        var json = JsonSerializer.Serialize(payload, AgentJsonContext.Default.MetricsPayload);
        var doc = JsonDocument.Parse(json);

        Assert.Equal(12.5, doc.RootElement.GetProperty("cpu").GetDouble());
        Assert.Equal(43.2, doc.RootElement.GetProperty("mem").GetDouble());
        Assert.Equal(61.8, doc.RootElement.GetProperty("disk").GetDouble());
        Assert.Equal(20480.0, doc.RootElement.GetProperty("netRx").GetDouble());
        Assert.Equal(4096.0, doc.RootElement.GetProperty("netTx").GetDouble());
    }
}
