using App.Core.Entities;

namespace App.Infrastructure.Workers;

/// <summary>
/// Retry decision for a failed outbox publish: bounded exponential backoff, never give up.
/// </summary>
public static class OutboxRetryPolicy
{
    /// <summary>Delay after the first failure.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound on the delay between attempts.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>From this many failed attempts on, each failure is logged at Error with the row id.</summary>
    public const int ErrorLogThreshold = 5;

    /// <summary>Delay before the next attempt, given the failure count after this failure (1-based).</summary>
    public static TimeSpan DelayAfterFailure(int retryCount)
    {
        var exponent = Math.Min(Math.Max(retryCount - 1, 0), 30);
        var seconds = Math.Min(MaxDelay.TotalSeconds, BaseDelay.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// Claim decision for one publisher cycle over unpublished rows in publish order. The relational path
/// applies the same rules in SQL (see <see cref="OutboxPublisherService"/>).
/// </summary>
public static class OutboxClaimPolicy
{
    /// <summary>
    /// Selects the rows this cycle may claim. A row is claimable when it is due (<c>NextAttemptAt</c> not in
    /// the future, which also covers rows leased by another publisher) and no earlier unpublished row of the
    /// same application exists. An earlier row blocks later rows even when it is leased or backing off, so
    /// per-application order holds across replicas.
    /// </summary>
    /// <param name="unpublishedInOrder">Unpublished rows ordered by CreatedAt, Id.</param>
    public static List<OutboxEvent> SelectClaimable(
        IEnumerable<OutboxEvent> unpublishedInOrder, DateTime now, int batchSize)
    {
        var selected = new List<OutboxEvent>();
        var blockedAggregates = new HashSet<Guid>();

        foreach (var outboxEvent in unpublishedInOrder)
        {
            var blocked = blockedAggregates.Contains(outboxEvent.AggregateId);

            if (!blocked && outboxEvent.NextAttemptAt <= now)
            {
                selected.Add(outboxEvent);
                if (selected.Count == batchSize)
                    break;
            }

            blockedAggregates.Add(outboxEvent.AggregateId);
        }

        return selected;
    }
}
