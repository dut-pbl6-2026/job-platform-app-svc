using App.Api.Services;
using App.Core.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SharedKernel.Events;
using SharedKernel.Kafka;
using Xunit;

namespace App.Tests;

/// <summary>
/// PBL6-34: application events must keep key = ApplicationId (partition ordering,
/// plan 2.2.1) and publishing must stay best-effort (never break application CRUD).
/// </summary>
public class ApplicationEventPublisherTests
{
    [Fact]
    public async Task PublishSubmittedAsync_UsesApplicationIdAsKeyAndSubmittedEventType()
    {
        var producer = new FakeProducer();
        var publisher = BuildPublisher(producer);
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/api/applications/cv/x.pdf");

        await publisher.PublishSubmittedAsync(app, "Senior .NET Dev");

        var call = Assert.Single(producer.Calls);
        Assert.Equal("application-events", call.Topic);
        Assert.Equal(app.Id.ToString(), call.Key);
        var envelope = Assert.IsType<EventEnvelope<ApplicationSubmittedEvent>>(call.Envelope);
        Assert.Equal(ApplicationEventTypes.Submitted, envelope.EventType);
        Assert.Equal(app.Id, envelope.Payload.ApplicationId);
        Assert.Equal(app.JobId, envelope.Payload.JobId);
        Assert.Equal(app.ApplicantId, envelope.Payload.ApplicantId);
        Assert.Equal("Senior .NET Dev", envelope.Payload.JobTitle);
    }

    [Fact]
    public async Task PublishStatusChangedAsync_UsesApplicationIdAsKeyAndStatusChangedEventType()
    {
        var producer = new FakeProducer();
        var publisher = BuildPublisher(producer);
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/api/applications/cv/x.pdf");
        var changedBy = Guid.NewGuid();

        await publisher.PublishStatusChangedAsync(app, "Pending", changedBy, "Senior .NET Dev");

        var call = Assert.Single(producer.Calls);
        Assert.Equal(app.Id.ToString(), call.Key);
        var envelope = Assert.IsType<EventEnvelope<ApplicationStatusChangedEvent>>(call.Envelope);
        Assert.Equal(ApplicationEventTypes.StatusChanged, envelope.EventType);
        Assert.Equal("Pending", envelope.Payload.PreviousStatus);
        Assert.Equal(app.Status.ToString(), envelope.Payload.NewStatus);
        Assert.Equal(changedBy, envelope.Payload.ChangedBy);
        Assert.Equal("Senior .NET Dev", envelope.Payload.JobTitle);
    }

    [Fact]
    public async Task PublishSubmittedAsync_WithEmptyTitle_StillPublishes()
    {
        var producer = new FakeProducer();
        var publisher = BuildPublisher(producer);
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/api/applications/cv/x.pdf");

        await publisher.PublishSubmittedAsync(app);

        var envelope = Assert.IsType<EventEnvelope<ApplicationSubmittedEvent>>(Assert.Single(producer.Calls).Envelope);
        Assert.Equal(string.Empty, envelope.Payload.JobTitle);
    }

    [Fact]
    public async Task PublishSubmittedAsync_WhenProducerThrows_DoesNotPropagate()
    {
        var producer = new FakeProducer { ThrowOnProduce = true };
        var publisher = BuildPublisher(producer);
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/api/applications/cv/x.pdf");

        var exception = await Record.ExceptionAsync(() => publisher.PublishSubmittedAsync(app, "title"));

        Assert.Null(exception);
    }

    private static ApplicationEventPublisher BuildPublisher(FakeProducer producer)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KAFKA_TOPIC_APPLICATION_EVENTS"] = "application-events",
            })
            .Build();

        return new ApplicationEventPublisher(producer, config, NullLogger<ApplicationEventPublisher>.Instance);
    }

    private sealed class FakeProducer : IKafkaProducer
    {
        public List<(string Topic, string Key, object Envelope)> Calls { get; } = new();

        public bool ThrowOnProduce { get; set; }

        public Task ProduceAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default)
        {
            if (ThrowOnProduce)
            {
                throw new InvalidOperationException("broker down");
            }

            Calls.Add((topic, key, envelope));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
