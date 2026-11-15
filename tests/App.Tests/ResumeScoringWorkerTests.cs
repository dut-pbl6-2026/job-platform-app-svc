using System.Text;
using App.Core.Entities;
using App.Core.Interfaces;
using App.Infrastructure.Data;
using App.Infrastructure.Services;
using App.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace App.Tests;

/// <summary>
/// Scoring cycles over the in-memory provider (no row locks, same selection rules as PostgreSQL) with the
/// real local CV storage and a fake ai-svc. Covers the score, retry, throttle, Failed-after-limit and lease rules.
/// The FOR UPDATE SKIP LOCKED path is covered by PostgresScoringClaimTests.
/// </summary>
public class ResumeScoringWorkerTests : IDisposable
{
    private static readonly byte[] CvBytes = "%PDF-1.4 worker test cv"u8.ToArray();

    private readonly ServiceProvider _services;
    private readonly FakeResumeScoringClient _client = new();
    private readonly CapturingLogger<ResumeScoringWorkerService> _logger = new();
    private readonly ResumeScoringWorkerService _worker;
    private readonly string _storageDir;

    public ResumeScoringWorkerTests()
    {
        _storageDir = Path.Combine(Path.GetTempPath(), "app_scoring_worker_" + Guid.NewGuid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_CV_STORAGE_PATH"] = _storageDir })
            .Build();
        var storage = new LocalCvStorageService(config, NullLogger<LocalCvStorageService>.Instance);

        var collection = new ServiceCollection();
        // The name must be fixed before AddDbContext: its options action runs once per scope.
        var databaseName = "ResumeScoring_" + Guid.NewGuid();
        collection.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(databaseName));
        collection.AddSingleton<IFileStorageService>(storage);
        _services = collection.BuildServiceProvider();

        _worker = new ResumeScoringWorkerService(_services.GetRequiredService<IServiceScopeFactory>(), _client, _logger);
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, true); } catch { }
        }
    }

    private AppDbContext NewDb() => _services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    /// <summary>Stores a CV and creates an application with its pending scoring job, as ApplyForJob does.</summary>
    private async Task<(Guid ApplicationId, Guid JobId)> ApplyAsync(byte[]? cvBytes = null, string? cvUrlOverride = null)
    {
        var storage = _services.GetRequiredService<IFileStorageService>();
        var cvUrl = cvUrlOverride
                    ?? await storage.SaveCvAsync(new MemoryStream(cvBytes ?? CvBytes), "cv.pdf", "application/pdf");

        var jobId = Guid.NewGuid();
        var application = new Application(jobId, Guid.NewGuid(), cvUrl);
        var db = NewDb();
        db.Applications.Add(application);
        db.ScoringJobs.Add(new ScoringJob(application.Id, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return (application.Id, jobId);
    }

    private async Task<ScoringJob> LoadAsync(Guid applicationId) =>
        await NewDb().ScoringJobs.AsNoTracking().SingleAsync(s => s.ApplicationId == applicationId);

    /// <summary>Moves the job's next attempt into the past, as if its backoff had elapsed.</summary>
    private async Task MakeDueAsync(Guid applicationId)
    {
        var db = NewDb();
        var job = await db.ScoringJobs.SingleAsync(s => s.ApplicationId == applicationId);
        job.Release(DateTime.UtcNow.AddSeconds(-1));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ScorePending_ScoresTheJobWithCvBytesAndJobId_AndMarksItDone()
    {
        var (applicationId, jobId) = await ApplyAsync();
        _client.Score = 84;

        var progressed = await _worker.ScorePendingAsync(CancellationToken.None);

        Assert.True(progressed);
        var call = Assert.Single(_client.Calls);
        Assert.Equal(jobId, call.JobId);
        Assert.Equal(CvBytes, call.CvBytes);
        Assert.EndsWith(".pdf", call.FileName);

        var job = await LoadAsync(applicationId);
        Assert.Equal(ScoringStatus.Done, job.Status);
        Assert.Equal(84, job.Score);
        Assert.NotNull(job.ScoredAt);
        Assert.Equal(0, job.AttemptCount);
    }

    [Fact]
    public async Task ScorePending_ServiceFailure_CountsAnAttemptAndSchedulesBackoff()
    {
        var (applicationId, _) = await ApplyAsync();
        _client.FailWith = new ResumeScoringException("ai-svc is unreachable.");
        var before = DateTime.UtcNow;

        await _worker.ScorePendingAsync(CancellationToken.None);

        var job = await LoadAsync(applicationId);
        Assert.Equal(ScoringStatus.Pending, job.Status);
        Assert.Equal(1, job.AttemptCount);
        Assert.Contains("unreachable", job.FailureReason);
        Assert.InRange(job.NextAttemptAt, before + ScoringRetryPolicy.DelayAfterFailure(1) - TimeSpan.FromSeconds(5),
            DateTime.UtcNow + ScoringRetryPolicy.DelayAfterFailure(1) + TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ScorePending_EightFailures_MarksFailedAndLogsAnErrorNamingTheRow()
    {
        var (applicationId, _) = await ApplyAsync();
        _client.FailWith = new ResumeScoringException("ai-svc returned HTTP 500.");

        for (var attempt = 1; attempt < ScoringRetryPolicy.MaxAttempts; attempt++)
        {
            await MakeDueAsync(applicationId);
            await _worker.ScorePendingAsync(CancellationToken.None);
        }
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(ScoringStatus.Pending, (await LoadAsync(applicationId)).Status);

        await MakeDueAsync(applicationId);
        await _worker.ScorePendingAsync(CancellationToken.None);

        var job = await LoadAsync(applicationId);
        Assert.Equal(ScoringStatus.Failed, job.Status);
        Assert.Equal(ScoringRetryPolicy.MaxAttempts, job.AttemptCount);
        Assert.Null(job.Score);

        var error = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(job.Id.ToString(), error.Message);

        // Failed rows are terminal: no further attempts.
        await MakeDueAsync(applicationId);
        Assert.False(await _worker.ScorePendingAsync(CancellationToken.None));
        Assert.Equal(ScoringRetryPolicy.MaxAttempts, (await LoadAsync(applicationId)).AttemptCount);
    }

    [Fact]
    public async Task ScorePending_ThrottledResponses_NeverMarkTheJobFailed()
    {
        var (applicationId, _) = await ApplyAsync();
        _client.FailWith = new ResumeScoringThrottledException("AI service rate limit reached (HTTP 429).");

        for (var i = 0; i < 20; i++)
        {
            await MakeDueAsync(applicationId);
            await _worker.ScorePendingAsync(CancellationToken.None);
        }

        var job = await LoadAsync(applicationId);
        Assert.Equal(ScoringStatus.Pending, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ScorePending_MissingCvFile_IsARecordedFailureNotASkip()
    {
        var (applicationId, _) = await ApplyAsync(cvUrlOverride: "/api/applications/cv/does-not-exist.pdf");

        await _worker.ScorePendingAsync(CancellationToken.None);

        var job = await LoadAsync(applicationId);
        Assert.Equal(1, job.AttemptCount);
        Assert.Contains("not found", job.FailureReason);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task ScorePending_ClientNotConfigured_ClaimsNothingAndKeepsRowsPending()
    {
        var (applicationId, _) = await ApplyAsync();
        var before = await LoadAsync(applicationId);
        _client.Enabled = false;

        Assert.False(await _worker.ScorePendingAsync(CancellationToken.None));
        Assert.False(await _worker.ScorePendingAsync(CancellationToken.None));

        var job = await LoadAsync(applicationId);
        Assert.Equal(ScoringStatus.Pending, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(before.NextAttemptAt, job.NextAttemptAt);
        Assert.Empty(_client.Calls);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ScorePending_LeasedJobIsNotClaimedByAnotherCycleWhileBeingScored()
    {
        var (applicationId, _) = await ApplyAsync();
        var nestedCycleResults = new List<bool>();
        _client.OnCall = async () => nestedCycleResults.Add(await _worker.ScorePendingAsync(CancellationToken.None));

        await _worker.ScorePendingAsync(CancellationToken.None);

        // The nested cycle runs while the first one holds the lease: it finds nothing to claim.
        Assert.Equal([false], nestedCycleResults);
        Assert.Single(_client.Calls);
        Assert.Equal(ScoringStatus.Done, (await LoadAsync(applicationId)).Status);
    }

    [Fact]
    public async Task ScorePending_ClaimsAtMostOneBatchPerCycle()
    {
        for (var i = 0; i < ResumeScoringWorkerService.BatchSize + 2; i++)
            await ApplyAsync();

        await _worker.ScorePendingAsync(CancellationToken.None);

        Assert.Equal(ResumeScoringWorkerService.BatchSize, _client.CallCount);
        var db = NewDb();
        Assert.Equal(2, await db.ScoringJobs.CountAsync(s => s.Status == ScoringStatus.Pending));
    }
}
