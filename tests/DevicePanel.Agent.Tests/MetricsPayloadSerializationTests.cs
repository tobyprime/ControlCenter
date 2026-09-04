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
        // 未携带扩展指标时 extra 不序列化（服务端把 null extra 视为非法负载）
        Assert.False(doc.RootElement.TryGetProperty("extra", out _));
    }

    [Fact]
    public void From_Sample_Builds_Snake_Case_Extra_Keys_For_New_Metrics()
    {
        var sample = new MetricsSample(
            11, 22, 33, 4400, 5500,
            MemUsedBytes: 4L * 1024 * 1024 * 1024,
            MemTotalBytes: 8L * 1024 * 1024 * 1024,
            DiskReadBytesPerSec: 2048,
            DiskWriteBytesPerSec: 4096,
            TempCelsius: 45.5,
            TempSensor: "coretemp Package id 0");

        var payload = MetricsPayload.From(sample);
        var json = JsonSerializer.Serialize(payload, AgentJsonContext.Default.MetricsPayload);
        var extra = JsonDocument.Parse(json).RootElement.GetProperty("extra");

        Assert.Equal(45.5, extra.GetProperty("temp").GetDouble());
        Assert.Equal("coretemp Package id 0", extra.GetProperty("temp_sensor").GetString());
        Assert.Equal(2048, extra.GetProperty("disk_rx").GetDouble(), precision: 0);
        Assert.Equal(4096, extra.GetProperty("disk_tx").GetDouble(), precision: 0);
        Assert.Equal(4L * 1024 * 1024 * 1024, extra.GetProperty("mem_used").GetDouble(), precision: 0);
        Assert.Equal(8L * 1024 * 1024 * 1024, extra.GetProperty("mem_total").GetDouble(), precision: 0);
    }

    [Fact]
    public void From_Sample_Without_Temperature_Omits_Temp_Keys_Only()
    {
        var sample = new MetricsSample(11, 22, 33, 0, 0, MemUsedBytes: 1024, MemTotalBytes: 2048);

        var payload = MetricsPayload.From(sample);
        var json = JsonSerializer.Serialize(payload, AgentJsonContext.Default.MetricsPayload);
        var extra = JsonDocument.Parse(json).RootElement.GetProperty("extra");

        Assert.False(extra.TryGetProperty("temp", out _));
        Assert.False(extra.TryGetProperty("temp_sensor", out _));
        Assert.Equal(1024, extra.GetProperty("mem_used").GetDouble(), precision: 0);
        Assert.Equal(2048, extra.GetProperty("mem_total").GetDouble(), precision: 0);
    }
}
