using App.Core.Entities;

namespace App.Infrastructure.Workers;

/// <summary>
/// Retry decisions for resume scoring. Counted failures back off exponentially, capped at
/// <see cref="MaxDelay"/>. After <see cref="MaxAttempts"/> counted failures the job is Failed and logged.
/// </summary>
public static class ScoringRetryPolicy
{
    /// <summary>Delay after the first counted failure.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on the delay between attempts.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    /// <summary>Counted failures after which the job is marked Failed.</summary>
    public const int MaxAttempts = 8;

    /// <summary>Delay before retrying a throttled (rate limited or busy) job. Not a counted attempt.</summary>
    public static readonly TimeSpan ThrottleDelay = TimeSpan.FromSeconds(60);

    /// <summary>Delay before the next attempt, given the counted failure count after this failure (1-based).</summary>
    public static TimeSpan DelayAfterFailure(int attemptCount)
    {
        var exponent = Math.Min(Math.Max(attemptCount - 1, 0), 30);
        var seconds = Math.Min(MaxDelay.TotalSeconds, BaseDelay.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// Claim decision for one worker cycle. The relational path applies the same rules in SQL
/// (<see cref="ResumeScoringWorkerService"/>).
/// </summary>
public static class ScoringClaimPolicy
{
    /// <summary>
    /// A job is claimable when it is Pending and due (<c>NextAttemptAt</c> not in the future). A leased job
    /// has its lease end in <c>NextAttemptAt</c>, so it is skipped until the lease expires.
    /// Ordered by due time, then id.
    /// </summary>
    public static List<ScoringJob> SelectClaimable(IEnumerable<ScoringJob> jobs, DateTime now, int batchSize) =>
        jobs
            .Where(j => j.Status == ScoringStatus.Pending && j.NextAttemptAt <= now)
            .OrderBy(j => j.NextAttemptAt).ThenBy(j => j.Id)
            .Take(batchSize)
            .ToList();
}
