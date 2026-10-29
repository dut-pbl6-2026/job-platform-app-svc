using System.Security.Cryptography;
using System.Text;
using App.Core.Entities;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Api.Endpoints;

/// <summary>
/// Service-to-service application stats endpoint used by the admin dashboard
/// aggregation (feat(admin) Slice 6). Not exposed through the gateway: callers must
/// present <c>Authorization: Bearer &lt;INTERNAL_SERVICE_TOKEN&gt;</c>.
/// </summary>
public static class InternalStatsEndpoints
{
    /// <summary>Configuration/env key holding the shared internal service token.</summary>
    public const string TokenConfigKey = "INTERNAL_SERVICE_TOKEN";

    private const string BearerPrefix = "Bearer ";

    public static WebApplication MapInternalStatsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/internal/stats", GetStats)
            .WithTags("Internal")
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> GetStats(
        HttpContext ctx,
        AppDbContext db,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger("InternalStatsEndpoints");
        var expected = config[TokenConfigKey];
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Fail closed: an unconfigured token must never turn the endpoint public.
            // Return 401 (not 503) — misconfigured token is an auth failure, not a service outage.
            logger.LogError(
                "{TokenKey} is not configured. Returning 401 to avoid exposing the endpoint.",
                TokenConfigKey);
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);
        }

        if (!IsAuthorized(ctx.Request.Headers.Authorization.ToString(), expected))
        {
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);
        }

        var applications = db.Applications.AsNoTracking();

        var counts = await applications
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Total, ct);

        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var newThisMonth = await applications.CountAsync(a => a.CreatedAt >= monthStart, ct);

        return Results.Ok(new InternalApplicationStats(
            Total: counts.Values.Sum(),
            Pending: counts.GetValueOrDefault(ApplicationStatus.Pending),
            Accepted: counts.GetValueOrDefault(ApplicationStatus.Accepted),
            Rejected: counts.GetValueOrDefault(ApplicationStatus.Rejected),
            NewThisMonth: newThisMonth));
    }

    private static bool IsAuthorized(string header, string expected)
    {
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = header[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            return false;
        }

        // Constant-time comparison — do not leak token length/prefix via timing.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(expected));
    }
}

/// <summary>Aggregated application counts for the admin dashboard.</summary>
public sealed record InternalApplicationStats(
    int Total,
    int Pending,
    int Accepted,
    int Rejected,
    int NewThisMonth);
