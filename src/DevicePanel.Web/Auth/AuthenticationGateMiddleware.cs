namespace DevicePanel.Web.Auth;

/// <summary>
/// 统一登录拦截：
/// - /healthz、/api/auth/login、/api/auth/logout 匿名可达；
/// - 其余 /api/* 未登录一律 401；
/// - 静态资源（带扩展名）放行给静态文件中间件；
/// - 其余视为 SPA 路由：未登录跳转 /login，已登录回退 index.html。
/// </summary>
public sealed class AuthenticationGateMiddleware
{
    private readonly RequestDelegate _next;

    public AuthenticationGateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISessionService sessions, AuthOptions options, IWebHostEnvironment environment)
    {
        var path = context.Request.Path;

        if (IsAlwaysAllowed(path))
        {
            await _next(context);
            return;
        }

        if (path.StartsWithSegments("/api"))
        {
            await RejectUnauthorizedApiAsync(context, sessions, options);
            return;
        }        if (Path.HasExtension(path.Value))
        {
            await _next(context);
            return;
        }

        // /login 匿名也要能渲染（SPA 登录页与主布局同壳）
        if (path.Value == "/login")
        {
            await ServeAppShellAsync(context, environment);
            return;
        }

        var token = context.Request.Cookies[options.SessionCookieName];
        var session = token is null ? null : sessions.Validate(token);
        if (session is null)
        {
            context.Response.Redirect("/login");
            return;
        }

        await ServeAppShellAsync(context, environment);
    }

    private static bool IsAlwaysAllowed(PathString path)
    {
        return path.StartsWithSegments("/healthz")
            || path.StartsWithSegments("/api/auth/login")
            || path.StartsWithSegments("/api/auth/logout");
    }

    private async Task RejectUnauthorizedApiAsync(HttpContext context, ISessionService sessions, AuthOptions options)
    {
        var token = context.Request.Cookies[options.SessionCookieName];
        var session = token is null ? null : sessions.Validate(token);
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "未登录或会话已过期" });
            return;
        }

        context.Items["SessionUsername"] = session.Username;
        await _next(context);
    }

    private static async Task ServeAppShellAsync(HttpContext context, IWebHostEnvironment environment)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        var shellPath = Path.Combine(environment.WebRootPath ?? "wwwroot", "index.html");
        if (!File.Exists(shellPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("前端资源未构建");
            return;
        }

        await context.Response.SendFileAsync(shellPath);
    }
}
