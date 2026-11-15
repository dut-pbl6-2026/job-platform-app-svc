using SharedKernel;

namespace App.Core.Entities;

/// <summary>
/// Transactional outbox row for one application event. It is inserted in the same <c>SaveChanges</c>
/// as the application change, so the event is committed with the change or not at all. The outbox
/// publisher sends the stored envelope to Kafka and marks the row published only after the broker
/// acknowledges it. A row is never deleted while unpublished.
/// </summary>
public class OutboxEvent : Entity
{
    public const int EventTypeMaxLength = 64;

    /// <summary>Event name, for example <c>application.submitted</c>.</summary>
    public string EventType { get; private set; } = string.Empty;

    /// <summary>JSON of the full <c>EventEnvelope</c>, stored as written so every retry sends the same EventId.</summary>
    public string Payload { get; private set; } = string.Empty;

    /// <summary>ApplicationId. Also the Kafka key, so one application stays ordered on one partition.</summary>
    public Guid AggregateId { get; private set; }

    /// <summary>True once Kafka has acknowledged the event.</summary>
    public bool IsPublished { get; private set; }

    /// <summary>UTC time the event was acknowledged by Kafka.</summary>
    public DateTime? PublishedAt { get; private set; }

    /// <summary>Failed publish attempts so far.</summary>
    public int RetryCount { get; private set; }

    /// <summary>
    /// UTC time before which the row is not claimed again. After a failure it is the backoff deadline; while
    /// a publisher holds the row it is the lease end. Both keep other replicas from claiming the row.
    /// </summary>
    public DateTime NextAttemptAt { get; private set; }

    // EF Core constructor
    protected OutboxEvent() { }

    public OutboxEvent(string eventType, string payload, Guid aggregateId)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("EventType is required.", nameof(eventType));
        if (eventType.Length > EventTypeMaxLength)
            throw new ArgumentException($"EventType cannot exceed {EventTypeMaxLength} characters.", nameof(eventType));
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Payload is required.", nameof(payload));
        if (aggregateId == Guid.Empty)
            throw new ArgumentException("AggregateId is required.", nameof(aggregateId));

        EventType = eventType.Trim();
        Payload = payload;
        AggregateId = aggregateId;
        NextAttemptAt = CreatedAt;
    }

    /// <summary>Marks the event as acknowledged by Kafka.</summary>
    public void MarkPublished(DateTime now)
    {
        IsPublished = true;
        PublishedAt = now;
    }

    /// <summary>Records a failed publish attempt and schedules the next one.</summary>
    public void RecordFailure(DateTime nextAttemptAt)
    {
        RetryCount++;
        NextAttemptAt = nextAttemptAt;
    }

    /// <summary>Holds the row until <paramref name="leaseEnd"/>. Other publishers skip it until then.</summary>
    public void Lease(DateTime leaseEnd) => NextAttemptAt = leaseEnd;

    /// <summary>Makes an unattempted row claimable again right away (used when a batch stops early).</summary>
    public void Release(DateTime now) => NextAttemptAt = now;
}
