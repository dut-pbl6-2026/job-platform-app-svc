using SharedKernel;

namespace App.Core.Entities;

/// <summary>
/// Scoring work for one application: the CV is compared with the job description by ai-svc, in the
/// background, after the applicant applies. One row per application, created in the same SaveChanges as
/// the application. Only recruiters see the score; applicants never see this row.
///
/// Lifecycle: Pending (claimable when <see cref="NextAttemptAt"/> is due) -> Done, or Failed after
/// <c>MaxAttempts</c> counted failures. Rows are never deleted.
/// </summary>
public class ScoringJob : Entity
{
    public const int FailureReasonMaxLength = 500;
    public const double MinScore = 0.0;
    public const double MaxScore = 100.0;

    public Guid ApplicationId { get; private set; }

    public Application? Application { get; private set; }

    public ScoringStatus Status { get; private set; } = ScoringStatus.Pending;

    /// <summary>Match score from 0 to 100 (ai-svc <c>overall_score</c>). Set only when <see cref="Status"/> is Done.</summary>
    public double? Score { get; private set; }

    /// <summary>UTC time the score was stored.</summary>
    public DateTime? ScoredAt { get; private set; }

    /// <summary>Reason for the last failed or throttled attempt. Cleared when the job is Done.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Counted failed attempts. Throttled responses are not counted.</summary>
    public int AttemptCount { get; private set; }

    /// <summary>
    /// UTC time before which the row is not claimed. After a failure it is the backoff deadline; while a
    /// worker holds the row it is the lease end. Both keep other replicas from claiming the row.
    /// </summary>
    public DateTime NextAttemptAt { get; private set; }

    // EF Core constructor
    protected ScoringJob() { }

    public ScoringJob(Guid applicationId, DateTime now)
    {
        if (applicationId == Guid.Empty)
            throw new ArgumentException("ApplicationId is required.", nameof(applicationId));

        ApplicationId = applicationId;
        NextAttemptAt = now;
    }

    /// <summary>Holds the row until <paramref name="leaseEnd"/>. Other workers skip it until then.</summary>
    public void Lease(DateTime leaseEnd) => NextAttemptAt = leaseEnd;

    /// <summary>Makes an unattempted row claimable again right away (used when a batch stops early).</summary>
    public void Release(DateTime now) => NextAttemptAt = now;

    /// <summary>Stores the score and ends the job.</summary>
    public void MarkDone(double score, DateTime now)
    {
        if (double.IsNaN(score) || double.IsInfinity(score) || score < MinScore || score > MaxScore)
            throw new ArgumentOutOfRangeException(nameof(score), $"Score must be between {MinScore} and {MaxScore}.");

        Status = ScoringStatus.Done;
        Score = score;
        ScoredAt = now;
        FailureReason = null;
        Touch();
    }

    /// <summary>
    /// Records a counted failed attempt. The job becomes Failed when the count reaches
    /// <paramref name="maxAttempts"/>; otherwise it is rescheduled for <paramref name="nextAttemptAt"/>.
    /// Returns true when this failure made the job Failed.
    /// </summary>
    public bool RecordFailure(string reason, DateTime nextAttemptAt, int maxAttempts)
    {
        AttemptCount++;
        FailureReason = Truncate(reason);
        NextAttemptAt = nextAttemptAt;
        if (AttemptCount >= maxAttempts)
            Status = ScoringStatus.Failed;
        Touch();
        return Status == ScoringStatus.Failed;
    }

    /// <summary>
    /// Records a throttled response (rate limit or busy service). The attempt is not counted, so a burst
    /// of applications cannot push a row to Failed. The row is rescheduled for <paramref name="nextAttemptAt"/>.
    /// </summary>
    public void RecordThrottle(string reason, DateTime nextAttemptAt)
    {
        FailureReason = Truncate(reason);
        NextAttemptAt = nextAttemptAt;
        Touch();
    }

    private static string Truncate(string reason)
    {
        var trimmed = reason.Trim();
        return trimmed.Length <= FailureReasonMaxLength ? trimmed : trimmed[..FailureReasonMaxLength];
    }
}
