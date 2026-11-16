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
                // Recruiter company, forwarded by the gateway from the JWT company_id claim.
                var company = context.Request.Headers["X-User-Company"].FirstOrDefault();
                if (Guid.TryParse(company, out var companyId) && companyId != Guid.Empty)
                    claims.Add(new Claim("company_id", companyId.ToString()));

                // Identity snapshotted on applications and used as the interview organiser. Forwarded by
                // the gateway from the verified token, never trusted from the client.
                var email = context.Request.Headers["X-User-Email"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(email))
                    claims.Add(new Claim(ClaimTypes.Email, email));

                var name = context.Request.Headers["X-User-Name"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(name))
                    claims.Add(new Claim("full_name", Uri.UnescapeDataString(name)));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Gateway"));
            }
        }

        await _next(context);
    }
}
