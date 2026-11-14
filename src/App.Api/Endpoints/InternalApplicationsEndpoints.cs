using App.Api.Auth;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Api.Endpoints;

/// <summary>
/// Service-to-service export of applications (PBL6-10). job-svc reads it once at startup to
/// backfill its recommendation read model with applications older than Kafka retention.
/// Not exposed through the gateway: callers must present
/// <c>Authorization: Bearer &lt;INTERNAL_SERVICE_TOKEN&gt;</c>.
/// </summary>
public static class InternalApplicationsEndpoints
{
    public const int DefaultSize = 100;
    public const int MaxSize = 100;

    public static WebApplication MapInternalApplicationsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/internal/applications", GetApplications)
            .WithTags("Internal")
            .ExcludeFromDescription();
        return app;
    }

    /// <summary>
    /// Returns every application (all statuses), oldest first (<c>CreatedAt</c>, then <c>Id</c>),
    /// paginated per api-conventions §5: 0-based <paramref name="page"/>, <paramref name="size"/> 1–100.
    /// Ids and <c>createdAt</c> only — no CV, cover letter, notes, score or status.
    /// </summary>
    public static async Task<IResult> GetApplications(
        HttpContext ctx,
        AppDbContext db,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        int? page,
        int? size,
        CancellationToken ct = default)
    {
        var denied = InternalServiceAuth.Authorize(
            ctx, config, loggerFactory.CreateLogger("InternalApplicationsEndpoints"));
        if (denied is not null)
        {
            return denied;
        }

        var p = page ?? 0;
        var s = size ?? DefaultSize;
        var errors = new Dictionary<string, string[]>();
        if (p < 0)
        {
            errors["page"] = ["page phải ≥ 0."];
        }

        if (s is < 1 or > MaxSize)
        {
            errors["size"] = [$"size phải trong khoảng 1–{MaxSize}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var query = db.Applications.AsNoTracking();
        var total = await query.CountAsync(ct);
        var offset = (long)p * s;
        List<InternalApplicationItem> items = offset >= total
            ? []
            : await query
                .OrderBy(a => a.CreatedAt)
                .ThenBy(a => a.Id)
                .Skip((int)offset)
                .Take(s)
                .Select(a => new InternalApplicationItem(a.Id, a.JobId, a.ApplicantId, a.CreatedAt))
                .ToListAsync(ct);

        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)s));
        return Results.Ok(new InternalApplicationsPage(items, total, p, s, totalPages));
    }
}

/// <summary>One exported application: who applied to which job, and when.</summary>
public sealed record InternalApplicationItem(Guid Id, Guid JobId, Guid ApplicantId, DateTime CreatedAt);

/// <summary>Page envelope per api-conventions §5.</summary>
public sealed record InternalApplicationsPage(
    IReadOnlyList<InternalApplicationItem> Items,
    int Total,
    int Page,
    int Size,
    int TotalPages);
