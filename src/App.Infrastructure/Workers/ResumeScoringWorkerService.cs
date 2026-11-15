using App.Core.Entities;
using App.Core.Interfaces;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure.Workers;

/// <summary>
/// BackgroundService that scores the CV of each new application against its job through ai-svc.
///
/// Each cycle runs in three steps:
/// 1. Claim (short transaction): due Pending rows are selected with <c>FOR UPDATE SKIP LOCKED</c> and leased
///    (<c>NextAttemptAt</c> moves to the lease end), then the transaction commits. Replicas never claim the
///    same row, so the worker scales horizontally.
/// 2. Score (no transaction open): the CV is read through <see cref="IFileStorageService"/> and sent to
///    ai-svc with a timeout.
/// 3. Mark: the score is stored and the row is Done. A counted failure is rescheduled with capped
///    exponential backoff; after <see cref="ScoringRetryPolicy.MaxAttempts"/> it is Failed and logged at Error
///    with its id. A throttled response is rescheduled without counting an attempt. Rows not attempted are
///    released at once.
///
/// A crash after ai-svc answered and before the mark scores the row again when its lease ends. The result
/// is the same score, so this is harmless. A row is never dropped: every outcome is stored on the row.
/// </summary>
public sealed class ResumeScoringWorkerService : BackgroundService
{
    private const int PollIntervalMs = 5_000;

    /// <summary>Rows claimed per cycle. Scored one at a time, so one cycle takes at most BatchSize HTTP timeouts.</summary>
    public const int BatchSize = 5;

    /// <summary>
    /// Longer than the worst case of one batch: BatchSize calls, each bounded by the client's HTTP timeout
    /// (60 s), plus the database work.
    /// </summary>
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IResumeScoringClient _client;
    private readonly ILogger<ResumeScoringWorkerService> _logger;
    private bool _disabledLogged;

    public ResumeScoringWorkerService(
        IServiceScopeFactory scopeFactory,
        IResumeScoringClient client,
        ILogger<ResumeScoringWorkerService> logger)
    {
        _scopeFactory = scopeFactory;
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ResumeScoringWorkerService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var progressed = false;
            try
            {
                progressed = await ScorePendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResumeScoringWorkerService: unexpected error in the scoring cycle. Retrying next cycle.");
            }

            if (progressed)
                continue; // backlog: score the next batch without waiting

            try
            {
                await Task.Delay(PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ResumeScoringWorkerService stopped.");
    }

    /// <summary>
    /// Runs one scoring cycle. Returns true when at least one job was scored in this cycle.
    /// Does nothing (and claims nothing) when the scoring client is not configured.
    /// </summary>
    public async Task<bool> ScorePendingAsync(CancellationToken ct)
    {
        if (!_client.Enabled)
        {
            if (!_disabledLogged)
            {
                _logger.LogWarning("ResumeScoringWorkerService: AI service is not configured. Set AI_SERVICE_URL and AI_SCORING_SERVICE_TOKEN. Pending scoring jobs wait until both are set.");
                _disabledLogged = true;
            }
            return false;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var now = DateTime.UtcNow;

        var batch = db.Database.IsRelational()
            ? await ClaimRelationalAsync(db, now, ct)
            : await ClaimInMemoryAsync(db, now, ct);

        return await ScoreBatchAsync(batch, db, storage, ct);
    }

    /// <summary>
    /// Claims and leases up to <see cref="BatchSize"/> due Pending rows in one short transaction. The SQL
    /// mirrors <see cref="ScoringClaimPolicy.SelectClaimable"/>. Table and column names must match the model
    /// (see ScoringSchemaTests).
    /// </summary>
    private async Task<List<ScoringJob>> ClaimRelationalAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var pending = ScoringStatus.Pending.ToString();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT s."Id" FROM scoring_jobs AS s
            WHERE s."Status" = {pending}
              AND s."NextAttemptAt" <= {now}
            ORDER BY s."NextAttemptAt", s."Id"
            LIMIT {BatchSize}
            FOR UPDATE OF s SKIP LOCKED
            """).ToListAsync(ct);

        var rows = ids.Count == 0
            ? []
            : await db.ScoringJobs.Include(s => s.Application).Where(s => ids.Contains(s.Id)).ToListAsync(ct);

        LeaseAll(rows, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return rows.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).ToList();
    }

    /// <summary>Non-relational providers (EF InMemory, used by tests) have no row locks. Same selection rules, no lock.</summary>
    private async Task<List<ScoringJob>> ClaimInMemoryAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var pending = await db.ScoringJobs
            .Include(s => s.Application)
            .Where(s => s.Status == ScoringStatus.Pending)
            .ToListAsync(ct);

        var batch = ScoringClaimPolicy.SelectClaimable(pending, now, BatchSize);
        LeaseAll(batch, now);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    private static void LeaseAll(IEnumerable<ScoringJob> rows, DateTime now)
    {
        var leaseEnd = now + LeaseDuration;
        foreach (var row in rows)
            row.Lease(leaseEnd);
    }

    private async Task<bool> ScoreBatchAsync(List<ScoringJob> batch, AppDbContext db, IFileStorageService storage, CancellationToken ct)
    {
        var progressed = false;

        for (var i = 0; i < batch.Count; i++)
        {
            try
            {
                if (await ScoreOneAsync(batch[i], storage, ct))
                    progressed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ReleaseRows(batch.Skip(i), DateTime.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }

        return progressed;
    }

    /// <summary>Scores one claimed job and records the outcome on it. Returns true when the job is Done.</summary>
    private async Task<bool> ScoreOneAsync(ScoringJob job, IFileStorageService storage, CancellationToken ct)
    {
        var application = job.Application;
        if (application is null)
        {
            RecordFailure(job, "Application row is missing for this scoring job.");
            return false;
        }

        try
        {
            var cv = await storage.GetCvAsync(Path.GetFileName(application.CvUrl), ct);
            if (cv is null)
            {
                RecordFailure(job, "CV file was not found in storage.");
                return false;
            }

            await using var stream = cv.Value.Stream;
            var score = await _client.ScoreAsync(stream, cv.Value.FileName, application.JobId, ct);
            job.MarkDone(score, DateTime.UtcNow);

            _logger.LogInformation(
                "ResumeScoringWorkerService: Scored ScoringJobId={ScoringJobId} ApplicationId={ApplicationId}.",
                job.Id, job.ApplicationId);
            return true;
        }
        catch (ResumeScoringThrottledException ex)
        {
            job.RecordThrottle(ex.Message, DateTime.UtcNow + ScoringRetryPolicy.ThrottleDelay);
            _logger.LogWarning(
                "ResumeScoringWorkerService: AI service throttled ScoringJobId={ScoringJobId}. Retrying at {NextAttemptAt:O}.",
                job.Id, job.NextAttemptAt);
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            RecordFailure(job, $"{ex.GetType().Name}: {ex.Message}", ex);
            return false;
        }
    }

    private void RecordFailure(ScoringJob job, string reason, Exception? ex = null)
    {
        var now = DateTime.UtcNow;
        var nextAttemptAt = now + ScoringRetryPolicy.DelayAfterFailure(job.AttemptCount + 1);
        var failed = job.RecordFailure(reason, nextAttemptAt, ScoringRetryPolicy.MaxAttempts);

        if (failed)
        {
            _logger.LogError(ex,
                "ResumeScoringWorkerService: Scoring job {ScoringJobId} for application {ApplicationId} marked Failed after {AttemptCount} attempts. Reason: {Reason}",
                job.Id, job.ApplicationId, job.AttemptCount, job.FailureReason);
        }
        else
        {
            _logger.LogWarning(ex,
                "ResumeScoringWorkerService: Scoring job {ScoringJobId} attempt {AttemptCount} failed. Retry at {NextAttemptAt:O}. Reason: {Reason}",
                job.Id, job.AttemptCount, job.NextAttemptAt, job.FailureReason);
        }
    }

    private static void ReleaseRows(IEnumerable<ScoringJob> rows, DateTime now)
    {
        foreach (var row in rows)
            row.Release(now);
    }
}
