using App.Core.Entities;
using App.Infrastructure.Workers;
using Xunit;

namespace App.Tests;

/// <summary>Backoff, claim selection, lease expiry and the ScoringJob state transitions (no database).</summary>
public class ResumeScoringPoliciesTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ScoringJob NewJob(DateTime nextAttemptAt, ScoringStatus status = ScoringStatus.Pending)
    {
        var job = new ScoringJob(Guid.NewGuid(), nextAttemptAt);
        if (status == ScoringStatus.Done)
            job.MarkDone(50, nextAttemptAt);
        else if (status == ScoringStatus.Failed)
            for (var i = 0; i < ScoringRetryPolicy.MaxAttempts; i++)
                job.RecordFailure("boom", nextAttemptAt, ScoringRetryPolicy.MaxAttempts);
        return job;
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 900)]
    [InlineData(8, 900)]
    [InlineData(1000, 900)]
    public void DelayAfterFailure_DoublesFromBaseAndCapsAtFifteenMinutes(int attemptCount, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ScoringRetryPolicy.DelayAfterFailure(attemptCount));
    }

    [Fact]
    public void Policy_HasTheDocumentedLimits()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ScoringRetryPolicy.BaseDelay);
        Assert.Equal(TimeSpan.FromMinutes(15), ScoringRetryPolicy.MaxDelay);
        Assert.Equal(8, ScoringRetryPolicy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(60), ScoringRetryPolicy.ThrottleDelay);
    }

    [Fact]
    public void LeaseDuration_CoversAFullBatchOfHttpTimeouts()
    {
        // Each scoring call is bounded by the 60 s client timeout (Program.cs); a batch scores its rows one by one.
        var worstCaseBatch = TimeSpan.FromSeconds(60 * ResumeScoringWorkerService.BatchSize);
        Assert.True(ResumeScoringWorkerService.LeaseDuration > worstCaseBatch);
    }

    [Fact]
    public void SelectClaimable_TakesOnlyDuePendingJobs_InDueOrder()
    {
        var overdue = NewJob(Now.AddMinutes(-5));
        var dueLater = NewJob(Now.AddMinutes(-1));
        var notDue = NewJob(Now.AddMinutes(1));
        var done = NewJob(Now.AddMinutes(-10), ScoringStatus.Done);
        var failed = NewJob(Now.AddMinutes(-10), ScoringStatus.Failed);

        var claimed = ScoringClaimPolicy.SelectClaimable([dueLater, notDue, done, overdue, failed], Now, 10);

        Assert.Equal([overdue.Id, dueLater.Id], claimed.Select(j => j.Id));
    }

    [Fact]
    public void SelectClaimable_RespectsBatchSize()
    {
        var jobs = Enumerable.Range(0, 7).Select(i => NewJob(Now.AddMinutes(-i - 1))).ToList();

        var claimed = ScoringClaimPolicy.SelectClaimable(jobs, Now, ResumeScoringWorkerService.BatchSize);

        Assert.Equal(ResumeScoringWorkerService.BatchSize, claimed.Count);
    }

    [Fact]
    public void LeaseExpiry_LeasedJobIsSkippedUntilTheLeaseEnds_ThenClaimable()
    {
        var job = NewJob(Now.AddMinutes(-1));
        var leaseEnd = Now + ResumeScoringWorkerService.LeaseDuration;
        job.Lease(leaseEnd);

        Assert.Empty(ScoringClaimPolicy.SelectClaimable([job], Now, 10));
        Assert.Empty(ScoringClaimPolicy.SelectClaimable([job], leaseEnd.AddSeconds(-1), 10));
        Assert.Equal([job.Id], ScoringClaimPolicy.SelectClaimable([job], leaseEnd, 10).Select(j => j.Id));
    }

    [Fact]
    public void Release_MakesAnUnattemptedJobClaimableAtOnce()
    {
        var job = NewJob(Now.AddMinutes(-1));
        job.Lease(Now.AddMinutes(10));

        job.Release(Now);

        Assert.Equal([job.Id], ScoringClaimPolicy.SelectClaimable([job], Now, 10).Select(j => j.Id));
    }

    [Fact]
    public void NewJob_IsPendingAndDueNow()
    {
        var job = new ScoringJob(Guid.NewGuid(), Now);

        Assert.Equal(ScoringStatus.Pending, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.Score);
        Assert.Equal(Now, job.NextAttemptAt);
    }

    [Fact]
    public void Constructor_RequiresApplicationId()
    {
        Assert.Throws<ArgumentException>(() => new ScoringJob(Guid.Empty, Now));
    }

    [Fact]
    public void MarkDone_StoresScoreAndEndsTheJob()
    {
        var job = NewJob(Now);
        job.RecordFailure("earlier failure", Now.AddMinutes(1), ScoringRetryPolicy.MaxAttempts);

        job.MarkDone(81.5, Now.AddSeconds(3));

        Assert.Equal(ScoringStatus.Done, job.Status);
        Assert.Equal(81.5, job.Score);
        Assert.Equal(Now.AddSeconds(3), job.ScoredAt);
        Assert.Null(job.FailureReason);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(100.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void MarkDone_RejectsScoresOutsideZeroToHundred(double score)
    {
        var job = NewJob(Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => job.MarkDone(score, Now));
        Assert.Equal(ScoringStatus.Pending, job.Status);
    }

    [Fact]
    public void RecordFailure_BecomesFailedOnlyWhenTheLimitIsReached()
    {
        var job = NewJob(Now);

        for (var attempt = 1; attempt < ScoringRetryPolicy.MaxAttempts; attempt++)
        {
            Assert.False(job.RecordFailure("boom", Now, ScoringRetryPolicy.MaxAttempts));
            Assert.Equal(ScoringStatus.Pending, job.Status);
        }

        Assert.True(job.RecordFailure("final boom", Now, ScoringRetryPolicy.MaxAttempts));
        Assert.Equal(ScoringStatus.Failed, job.Status);
        Assert.Equal(ScoringRetryPolicy.MaxAttempts, job.AttemptCount);
        Assert.Equal("final boom", job.FailureReason);
    }

    [Fact]
    public void RecordFailure_TruncatesLongReasons()
    {
        var job = NewJob(Now);

        job.RecordFailure(new string('x', 2000), Now, ScoringRetryPolicy.MaxAttempts);

        Assert.Equal(ScoringJob.FailureReasonMaxLength, job.FailureReason!.Length);
    }

    [Fact]
    public void RecordThrottle_ReschedulesWithoutCountingAnAttempt()
    {
        var job = NewJob(Now);

        for (var i = 0; i < 50; i++)
            job.RecordThrottle("rate limited", Now.AddSeconds(60));

        Assert.Equal(0, job.AttemptCount);
        Assert.Equal(ScoringStatus.Pending, job.Status);
        Assert.Equal(Now.AddSeconds(60), job.NextAttemptAt);
        Assert.Equal("rate limited", job.FailureReason);
    }
}
