namespace DevicePanel.Web.Endpoints;

/// <summary>GET /healthz 响应体（文档化响应类型标注，JSON 形状与原匿名对象一致）。</summary>
public sealed record HealthResponse(string Status);

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok"))).Produces<HealthResponse>();
        return endpoints;
    }
}
