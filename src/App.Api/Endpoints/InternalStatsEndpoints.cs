using App.Api.Auth;
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
    public const string TokenConfigKey = InternalServiceAuth.TokenConfigKey;

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
        var denied = InternalServiceAuth.Authorize(
            ctx, config, loggerFactory.CreateLogger("InternalStatsEndpoints"));
        if (denied is not null)
        {
            return denied;
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
}

/// <summary>Aggregated application counts for the admin dashboard.</summary>
public sealed record InternalApplicationStats(
    int Total,
    int Pending,
    int Accepted,
    int Rejected,
    int NewThisMonth);
