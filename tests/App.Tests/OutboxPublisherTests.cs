using System.Text.Json;
using App.Api.Services;
using App.Core.Entities;
using App.Infrastructure.Data;
using App.Infrastructure.Outbox;
using App.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharedKernel.Events;
using SharedKernel.Kafka;
using Xunit;

namespace App.Tests;

/// <summary>
/// Publish cycle over the in-memory provider (no row locks, same selection rules as PostgreSQL). Covers
/// acknowledgement-then-mark, retry without losing the row, the same EventId on every retry, and ordering.
/// The FOR UPDATE SKIP LOCKED path needs PostgreSQL and is verified in the infra E2E stack.
/// </summary>
public class OutboxPublisherTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly RecordingKafkaProducer _producer = new();
    private readonly OutboxPublisherService _publisher;

    public OutboxPublisherTests()
    {
        // The name must be fixed before AddDbContext: its options action runs once per scope.
        var databaseName = "OutboxPublisher_" + Guid.NewGuid();
        var collection = new ServiceCollection();
        collection.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(databaseName));
        _services = collection.BuildServiceProvider();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        _publisher = new OutboxPublisherService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _producer,
            config,
            NullLogger<OutboxPublisherService>.Instance);
    }

    public void Dispose() => _services.Dispose();

    private AppDbContext NewDb() => _services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private async Task<Guid> AddRowAsync(OutboxEvent row)
    {
        var db = NewDb();
        db.OutboxEvents.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task<OutboxEvent> LoadAsync(Guid id) =>
        await NewDb().OutboxEvents.AsNoTracking().SingleAsync(e => e.Id == id);

    private async Task MakeDueAsync(Guid id)
    {
        var db = NewDb();
        var row = await db.OutboxEvents.SingleAsync(e => e.Id == id);
        row.Release(DateTime.UtcNow.AddSeconds(-1));
        await db.SaveChangesAsync();
    }

    private static Application NewApplication() => new(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");

    private static OutboxEvent SubmittedRow(Application application) =>
        ApplicationOutboxEvents.Submitted(application, "Title");

    [Fact]
    public async Task PublishPending_Acknowledged_SendsStoredEnvelopeWithApplicationKeyAndMarksPublished()
    {
        var application = NewApplication();
        var row = SubmittedRow(application);
        var storedEventId = JsonSerializer.Deserialize<EventEnvelope<ApplicationSubmittedEvent>>(row.Payload, KafkaJson.Options)!.EventId;
        var id = await AddRowAsync(row);

        var progressed = await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.True(progressed);
        var sent = Assert.Single(_producer.Sent);
        Assert.Equal("application-events", sent.Topic);
        Assert.Equal(application.Id.ToString(), sent.Key);
        var envelope = Assert.IsType<EventEnvelope<ApplicationSubmittedEvent>>(sent.Envelope);
        Assert.Equal(storedEventId, envelope.EventId);
        Assert.Equal(ApplicationEventTypes.Submitted, envelope.EventType);

        var stored = await LoadAsync(id);
        Assert.True(stored.IsPublished);
        Assert.NotNull(stored.PublishedAt);
    }

    [Fact]
    public async Task PublishPending_ProduceFails_KeepsRowUnpublishedWithBackoff()
    {
        var id = await AddRowAsync(SubmittedRow(NewApplication()));
        _producer.FailuresRemaining = 1;

        var progressed = await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.False(progressed);
        var stored = await LoadAsync(id);
        Assert.False(stored.IsPublished);
        Assert.Equal(1, stored.RetryCount);
        Assert.True(stored.NextAttemptAt > DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task PublishPending_RowInBackoff_IsNotSentAgainImmediately()
    {
        await AddRowAsync(SubmittedRow(NewApplication()));
        _producer.FailuresRemaining = 1;
        await _publisher.PublishPendingAsync(CancellationToken.None);

        var progressed = await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.False(progressed);
        Assert.Equal(1, _producer.Attempts);
    }

    [Fact]
    public async Task PublishPending_RetryAfterFailure_SendsSameEventIdAndPublishes()
    {
        var id = await AddRowAsync(SubmittedRow(NewApplication()));
        _producer.FailuresRemaining = 1;
        await _publisher.PublishPendingAsync(CancellationToken.None);
        var firstAttemptEventId = JsonSerializer.Deserialize<EventEnvelope<ApplicationSubmittedEvent>>(
            (await LoadAsync(id)).Payload, KafkaJson.Options)!.EventId;

        await MakeDueAsync(id);
        var progressed = await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.True(progressed);
        var envelope = Assert.IsType<EventEnvelope<ApplicationSubmittedEvent>>(Assert.Single(_producer.Sent).Envelope);
        Assert.Equal(firstAttemptEventId, envelope.EventId);
        Assert.True((await LoadAsync(id)).IsPublished);
    }

    [Fact]
    public async Task PublishPending_FailureStopsBatchAndReleasesUnattemptedRows()
    {
        var first = await AddRowAsync(SubmittedRow(NewApplication()));
        var second = await AddRowAsync(SubmittedRow(NewApplication()));
        _producer.FailuresRemaining = 1;

        await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(1, _producer.Attempts);
        Assert.Empty(_producer.Sent);
        Assert.Equal(1, (await LoadAsync(first)).RetryCount);
        var untouched = await LoadAsync(second);
        Assert.False(untouched.IsPublished);
        Assert.Equal(0, untouched.RetryCount);
        Assert.True(untouched.NextAttemptAt <= DateTime.UtcNow);
    }

    [Fact]
    public async Task PublishPending_LaterRowOfSameApplicationWaitsForEarlierRow()
    {
        var app = NewApplication();
        var submitted = await AddRowAsync(SubmittedRow(app));
        app.UpdateStatus(ApplicationStatus.Reviewed, Guid.NewGuid(), null);
        await AddRowAsync(ApplicationOutboxEvents.StatusChanged(app, "Pending", Guid.NewGuid(), "Title"));
        _producer.FailuresRemaining = 1;

        await _publisher.PublishPendingAsync(CancellationToken.None);
        await MakeDueAsync(submitted); // earlier row becomes due, the later row is still behind it
        _producer.FailuresRemaining = 0;
        await _publisher.PublishPendingAsync(CancellationToken.None);
        await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(new[] { ApplicationEventTypes.Submitted, ApplicationEventTypes.StatusChanged }, _producer.SentEventTypes);
    }

    [Fact]
    public async Task PublishPending_LeasedRowIsNotClaimedByAnotherPublisher()
    {
        var id = await AddRowAsync(SubmittedRow(NewApplication()));
        var db = NewDb();
        var row = await db.OutboxEvents.SingleAsync(e => e.Id == id);
        row.Lease(DateTime.UtcNow.AddMinutes(2));
        await db.SaveChangesAsync();

        var progressed = await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.False(progressed);
        Assert.Equal(0, _producer.Attempts);
    }

    [Fact]
    public async Task PublishPending_UnconfiguredKafka_NeverMarksRowPublished()
    {
        var id = await AddRowAsync(SubmittedRow(NewApplication()));
        var publisher = new OutboxPublisherService(
            _services.GetRequiredService<IServiceScopeFactory>(),
            new UnconfiguredKafkaProducer(),
            new ConfigurationBuilder().Build(),
            NullLogger<OutboxPublisherService>.Instance);

        var progressed = await publisher.PublishPendingAsync(CancellationToken.None);

        Assert.False(progressed);
        var stored = await LoadAsync(id);
        Assert.False(stored.IsPublished);
        Assert.Equal(1, stored.RetryCount);
    }

    [Fact]
    public async Task PublishPending_DeletesOnlyPublishedRowsOlderThanRetention()
    {
        var oldPublished = await AddRowAsync(SubmittedRow(NewApplication()));
        var unpublished = await AddRowAsync(SubmittedRow(NewApplication()));
        var db = NewDb();
        var old = await db.OutboxEvents.SingleAsync(e => e.Id == oldPublished);
        old.MarkPublished(DateTime.UtcNow.AddDays(-30));
        await db.SaveChangesAsync();

        await _publisher.PublishPendingAsync(CancellationToken.None);

        Assert.Null(await NewDb().OutboxEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == oldPublished));
        Assert.NotNull(await NewDb().OutboxEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == unpublished));
    }
}
