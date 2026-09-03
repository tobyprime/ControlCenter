using DevicePanel.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace DevicePanel.Web.Auth;

public sealed record LoginRequest(string Username, string Password);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");

        auth.MapPost("/login", async (
            [FromBody] LoginRequest request,
            ILoginRateLimiter rateLimiter,
            IPasswordHasher passwordHasher,
            ISessionService sessions,
            SqliteConnectionFactory connectionFactory,
            AuthOptions options,
            HttpContext http) =>
        {
            var username = request.Username.Trim();
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(request.Password))
            {
                return Results.BadRequest(new { error = "请输入用户名和密码" });
            }

            if (rateLimiter.IsLocked(username, out var remaining))
            {
                return Results.Json(
                    new { error = $"登录失败次数过多，请约 {(int)Math.Ceiling(remaining.TotalSeconds)} 秒后重试" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            var passwordOk = VerifyPassword(connectionFactory, passwordHasher, username, request.Password);
            if (!passwordOk)
            {
                rateLimiter.RecordFailure(username);
                return Results.Json(
                    new { error = "用户名或密码错误" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            rateLimiter.Reset(username);
            var token = sessions.Create(username);
            var expiresUtc = DateTimeOffset.UtcNow.AddHours(options.SessionHours);
            http.Response.Cookies.Append(options.SessionCookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                IsEssential = true,
                Expires = expiresUtc,
            });
            return Results.Ok(new { username });
        });

        auth.MapPost("/logout", (ISessionService sessions, AuthOptions options, HttpContext http) =>
        {
            var token = http.Request.Cookies[options.SessionCookieName];
            if (!string.IsNullOrEmpty(token))
            {
                sessions.Invalidate(token);
            }

            http.Response.Cookies.Delete(options.SessionCookieName, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                IsEssential = true,
            });
            return Results.NoContent();
        });

        auth.MapGet("/me", (ISessionService sessions, AuthOptions options, HttpContext http) =>
        {
            var token = http.Request.Cookies[options.SessionCookieName];
            var session = token is null ? null : sessions.Validate(token);
            if (session is null)
            {
                return Results.Json(new { error = "未登录" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(new { username = session.Username, expiresAtUtc = session.ExpiresAtUtc });
        });

        return endpoints;
    }

    private static bool VerifyPassword(
        SqliteConnectionFactory connectionFactory,
        IPasswordHasher passwordHasher,
        string username,
        string password)
    {
        using var connection = connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT password_hash FROM users WHERE username = $username";
        command.Parameters.AddWithValue("$username", username);
        var storedHash = command.ExecuteScalar() as string;
        return storedHash is not null && passwordHasher.Verify(password, storedHash);
    }
}
