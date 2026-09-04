using DevicePanel.Web.Dashboard;

namespace DevicePanel.Web.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var dashboard = endpoints.MapGroup("/api/dashboard");

        dashboard.MapGet("/layout", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        dashboard.MapPut("/layout", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

        return endpoints;
    }
}
