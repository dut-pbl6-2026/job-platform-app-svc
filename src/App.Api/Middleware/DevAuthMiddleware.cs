using System.Security.Claims;

namespace App.Api.Middleware;

/// <summary>
/// Development-only middleware that extracts user ID and role from headers
/// forwarded by the API Gateway or set via test clients (Postman/curl).
/// In Production, JWT Bearer authentication is strictly enforced.
/// </summary>
public class DevAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<DevAuthMiddleware> _logger;
    private readonly bool _enabled;

    public DevAuthMiddleware(
        RequestDelegate next,
        ILogger<DevAuthMiddleware> logger,
        IConfiguration config,
        IWebHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _enabled = env.IsDevelopment() &&
                   (config.GetValue<bool>("ENABLE_DEV_AUTH") ||
                    string.Equals(Environment.GetEnvironmentVariable("ENABLE_DEV_AUTH"), "true", StringComparison.OrdinalIgnoreCase));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_enabled)
        {
            var userId = context.Request.Headers["X-User-Id"].FirstOrDefault();
            var role = context.Request.Headers["X-User-Role"].FirstOrDefault()
                       ?? context.Request.Headers["X-Role"].FirstOrDefault()
                       ?? "User";

            if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out _))
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, userId),
                    new(ClaimTypes.Role, role)
                };
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Gateway"));
            }
        }

        await _next(context);
    }
}
