using System.Text.Json;
using App.Core.Entities;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace App.Infrastructure.Workers;

/// <summary>
/// BackgroundService that publishes <see cref="OutboxEvent"/> rows to the application-events topic.
///
/// Each cycle runs in three steps:
/// 1. Claim (short transaction): due rows are selected with <c>FOR UPDATE SKIP LOCKED</c> and leased
///    (<c>NextAttemptAt</c> moves to the lease end), then the transaction commits.
/// 2. Send (no transaction open): each row is produced with its stored envelope, so the EventId is the
///    same on every retry.
/// 3. Mark: a row is marked published only after Kafka acknowledges it. On failure it is rescheduled with
///    exponential backoff. Rows that were not attempted are released at once.
///
/// Delivery is at-least-once: a crash after the acknowledgement and before the mark republishes the row
/// when its lease ends, with the same EventId. notif-svc deduplicates notifications on
/// (application, event type, status), so a republished event does not send a second email.
/// A row is never dropped. Rows that keep failing are logged at Error with their id.
/// Ordering: a row waits while an earlier unpublished row of the same application exists.
/// </summary>
public sealed class OutboxPublisherService : BackgroundService
{
    private const int PollIntervalMs = 5_000;
    private const int BatchSize = 10;
    private const string DefaultTopic = "application-events";

    // Longer than the worst case of one batch: each send is bounded by the producer's 10 s message timeout.
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PublishedRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKafkaProducer _producer;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly string _topic;
    private DateTime _nextCleanupAt = DateTime.MinValue;

    public OutboxPublisherService(
        IServiceScopeFactory scopeFactory,
        IKafkaProducer producer,
        IConfiguration configuration,
        ILogger<OutboxPublisherService> logger)
    {
        _scopeFactory = scopeFactory;
        _producer = producer;
        _logger = logger;
        _topic = (configuration["KAFKA_TOPIC_APPLICATION_EVENTS"]
            ?? configuration["Kafka:Topic"]
            ?? DefaultTopic).Trim();
        if (string.IsNullOrWhiteSpace(_topic))
        {
            _topic = DefaultTopic;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxPublisherService started. Topic={Topic}.", _topic);

        while (!stoppingToken.IsCancellationRequested)
        {
            var progressed = false;
            try
            {
                progressed = await PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxPublisherService: unexpected error in the publish cycle. Retrying next cycle.");
            }

            if (progressed)
                continue; // backlog: publish the next batch without waiting

            try
            {
                await Task.Delay(PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxPublisherService stopped.");
    }

    /// <summary>
    /// Runs one publish cycle. Returns true when at least one row was acknowledged by Kafka in this cycle.
    /// </summary>
    public async Task<bool> PublishPendingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var batch = db.Database.IsRelational()
            ? await ClaimRelationalAsync(db, now, ct)
            : await ClaimInMemoryAsync(db, now, ct);

        var progressed = await PublishBatchAsync(batch, db, ct);

        if (now >= _nextCleanupAt)
        {
            await CleanupPublishedAsync(db, now, ct);
            _nextCleanupAt = now + CleanupInterval;
        }

        return progressed;
    }

    /// <summary>
    /// Claims and leases up to <see cref="BatchSize"/> due rows in one short transaction. The SQL mirrors
    /// <see cref="OutboxClaimPolicy.SelectClaimable"/>: a row is skipped while an earlier unpublished row of
    /// the same application exists. Table and column names must match the model (see the migration test).
    /// </summary>
    private static async Task<List<OutboxEvent>> ClaimRelationalAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT o."Id" FROM outbox_events AS o
            WHERE NOT o."IsPublished"
              AND o."NextAttemptAt" <= {now}
              AND NOT EXISTS (
                  SELECT 1 FROM outbox_events AS p
                  WHERE NOT p."IsPublished"
                    AND p."AggregateId" = o."AggregateId"
                    AND (p."CreatedAt", p."Id") < (o."CreatedAt", o."Id"))
            ORDER BY o."CreatedAt", o."Id"
            LIMIT {BatchSize}
            FOR UPDATE OF o SKIP LOCKED
            """).ToListAsync(ct);

        var rows = ids.Count == 0
            ? []
            : await db.OutboxEvents.Where(e => ids.Contains(e.Id)).ToListAsync(ct);

        LeaseAll(rows, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return rows.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToList();
    }

    /// <summary>
    /// Non-relational providers (EF InMemory, used by tests) have no row locks. Same selection rules, no lock.
    /// </summary>
    private static async Task<List<OutboxEvent>> ClaimInMemoryAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var unpublished = await db.OutboxEvents
            .Where(e => !e.IsPublished)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .ToListAsync(ct);

        var batch = OutboxClaimPolicy.SelectClaimable(unpublished, now, BatchSize);
        LeaseAll(batch, now);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    private static void LeaseAll(IEnumerable<OutboxEvent> rows, DateTime now)
    {
        var leaseEnd = now + LeaseDuration;
        foreach (var row in rows)
            row.Lease(leaseEnd);
    }

    /// <summary>
    /// Sends the claimed rows in order and stops at the first failure, so a Kafka outage costs one publish
    /// timeout per cycle. Returns true when at least one row was acknowledged.
    /// </summary>
    private async Task<bool> PublishBatchAsync(List<OutboxEvent> batch, AppDbContext db, CancellationToken ct)
    {
        var progressed = false;

        for (var i = 0; i < batch.Count; i++)
        {
            var row = batch[i];

            try
            {
                await ProduceAsync(row, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ReleaseRows(batch.Skip(i), DateTime.UtcNow);
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                var now = DateTime.UtcNow;
                row.RecordFailure(now + OutboxRetryPolicy.DelayAfterFailure(row.RetryCount + 1));
                LogFailure(row, ex);

                ReleaseRows(batch.Skip(i + 1), now);
                await db.SaveChangesAsync(CancellationToken.None);
                return progressed;
            }

            row.MarkPublished(DateTime.UtcNow);
            await db.SaveChangesAsync(ct);
            progressed = true;

            _logger.LogDebug(
                "OutboxPublisherService: Published {EventType} ApplicationId={ApplicationId} OutboxId={OutboxId}.",
                row.EventType, row.AggregateId, row.Id);
        }

        return progressed;
    }

    private static void ReleaseRows(IEnumerable<OutboxEvent> rows, DateTime now)
    {
        foreach (var row in rows)
            row.Release(now);
    }

    private async Task ProduceAsync(OutboxEvent row, CancellationToken ct)
    {
        var key = row.AggregateId.ToString();

        switch (row.EventType)
        {
            case ApplicationEventTypes.Submitted:
                await _producer.ProduceAsync(_topic, key, Deserialize<ApplicationSubmittedEvent>(row), ct);
                return;

            case ApplicationEventTypes.StatusChanged:
                await _producer.ProduceAsync(_topic, key, Deserialize<ApplicationStatusChangedEvent>(row), ct);
                return;

            default:
                throw new InvalidOperationException($"Unsupported outbox event type '{row.EventType}'.");
        }
    }

    /// <summary>Reads the stored envelope back as written, so the EventId and timestamp are kept.</summary>
    private static EventEnvelope<T> Deserialize<T>(OutboxEvent row) =>
        JsonSerializer.Deserialize<EventEnvelope<T>>(row.Payload, KafkaJson.Options)
        ?? throw new InvalidOperationException($"Outbox row {row.Id} has an empty payload.");

    private void LogFailure(OutboxEvent row, Exception ex)
    {
        if (row.RetryCount >= OutboxRetryPolicy.ErrorLogThreshold)
        {
            _logger.LogError(ex,
                "OutboxPublisherService: Event {OutboxId} ({EventType}) ApplicationId={ApplicationId} has failed {RetryCount} times. " +
                "Row kept unpublished; next attempt at {NextAttemptAt:O}.",
                row.Id, row.EventType, row.AggregateId, row.RetryCount, row.NextAttemptAt);
        }
        else
        {
            _logger.LogWarning(ex,
                "OutboxPublisherService: Failed to publish {EventType} OutboxId={OutboxId}. RetryCount={RetryCount}.",
                row.EventType, row.Id, row.RetryCount);
        }
    }

    private static async Task CleanupPublishedAsync(AppDbContext db, DateTime now, CancellationToken ct)
    {
        var cutoff = now - PublishedRetention;
        var stale = db.OutboxEvents.Where(e => e.IsPublished && e.PublishedAt < cutoff);

        if (db.Database.IsRelational())
        {
            await stale.ExecuteDeleteAsync(ct);
            return;
        }

        db.OutboxEvents.RemoveRange(await stale.ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}
