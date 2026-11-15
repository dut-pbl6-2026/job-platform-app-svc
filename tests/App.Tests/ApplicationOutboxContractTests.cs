using System.Text.Json;
using App.Core.Entities;
using App.Infrastructure.Outbox;
using SharedKernel.Events;
using SharedKernel.Kafka;
using Xunit;

namespace App.Tests;

/// <summary>
/// Wire contract for application.submitted and application.status_changed. The stored outbox envelope is what
/// notif-svc reads, so its JSON names and value kinds must not change: ApplicationId, JobTitle, ApplicantId,
/// PreviousStatus, NewStatus, EventId and EventType.
/// </summary>
public class ApplicationOutboxContractTests
{
    private static readonly string[] EnvelopeKeys = ["eventId", "eventType", "version", "timestamp", "payload"];

    private static readonly string[] SubmittedPayloadKeys =
        ["applicationId", "jobId", "jobTitle", "applicantId", "occurredAt"];

    private static readonly string[] StatusChangedPayloadKeys =
        ["applicationId", "jobId", "jobTitle", "applicantId", "previousStatus", "newStatus", "changedBy", "occurredAt"];

    private static Application NewApplication() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "/api/applications/cv/contract.pdf", "cover");

    private static JsonElement ParsePayload(OutboxEvent row) =>
        JsonDocument.Parse(row.Payload).RootElement.Clone();

    [Fact]
    public void Submitted_EnvelopeAndPayloadKeysAndKindsAreUnchanged()
    {
        var app = NewApplication();
        var row = ApplicationOutboxEvents.Submitted(app, "Senior .NET Developer");
        var root = ParsePayload(row);

        Assert.Equal(EnvelopeKeys.OrderBy(k => k), root.EnumerateObject().Select(p => p.Name).OrderBy(k => k));
        Assert.Equal(JsonValueKind.String, root.GetProperty("eventId").ValueKind);
        Assert.Equal(ApplicationEventTypes.Submitted, root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("timestamp").ValueKind);

        var payload = root.GetProperty("payload");
        Assert.Equal(SubmittedPayloadKeys.OrderBy(k => k), payload.EnumerateObject().Select(p => p.Name).OrderBy(k => k));
        Assert.Equal(app.Id, payload.GetProperty("applicationId").GetGuid());
        Assert.Equal(app.JobId, payload.GetProperty("jobId").GetGuid());
        Assert.Equal("Senior .NET Developer", payload.GetProperty("jobTitle").GetString());
        Assert.Equal(app.ApplicantId, payload.GetProperty("applicantId").GetGuid());
        Assert.Equal(JsonValueKind.String, payload.GetProperty("occurredAt").ValueKind);
    }

    [Fact]
    public void StatusChanged_EnvelopeAndPayloadKeysAndKindsAreUnchanged()
    {
        var app = NewApplication();
        app.UpdateStatus(App.Core.Entities.ApplicationStatus.Reviewed, Guid.NewGuid(), null);
        var changedBy = Guid.NewGuid();
        var row = ApplicationOutboxEvents.StatusChanged(app, "Pending", changedBy, "Senior .NET Developer");
        var root = ParsePayload(row);

        Assert.Equal(EnvelopeKeys.OrderBy(k => k), root.EnumerateObject().Select(p => p.Name).OrderBy(k => k));
        Assert.Equal(ApplicationEventTypes.StatusChanged, root.GetProperty("eventType").GetString());

        var payload = root.GetProperty("payload");
        Assert.Equal(StatusChangedPayloadKeys.OrderBy(k => k), payload.EnumerateObject().Select(p => p.Name).OrderBy(k => k));
        Assert.Equal(app.Id, payload.GetProperty("applicationId").GetGuid());
        Assert.Equal("Senior .NET Developer", payload.GetProperty("jobTitle").GetString());
        Assert.Equal(app.ApplicantId, payload.GetProperty("applicantId").GetGuid());
        Assert.Equal("Pending", payload.GetProperty("previousStatus").GetString());
        Assert.Equal("Reviewed", payload.GetProperty("newStatus").GetString());
        Assert.Equal(changedBy, payload.GetProperty("changedBy").GetGuid());
    }

    [Fact]
    public void Rows_RoundTripThroughKafkaJsonIntoTheSharedContracts()
    {
        var app = NewApplication();
        var submitted = ApplicationOutboxEvents.Submitted(app, "Title");
        var statusChanged = ApplicationOutboxEvents.StatusChanged(app, "Pending", Guid.NewGuid(), "Title");

        var sub = JsonSerializer.Deserialize<EventEnvelope<ApplicationSubmittedEvent>>(submitted.Payload, KafkaJson.Options)!;
        Assert.Equal(ApplicationEventTypes.Submitted, sub.EventType);
        Assert.Equal(app.Id, sub.Payload.ApplicationId);
        Assert.Equal("Title", sub.Payload.JobTitle);

        var status = JsonSerializer.Deserialize<EventEnvelope<ApplicationStatusChangedEvent>>(statusChanged.Payload, KafkaJson.Options)!;
        Assert.Equal(ApplicationEventTypes.StatusChanged, status.EventType);
        Assert.Equal("Pending", status.Payload.PreviousStatus);
        Assert.Equal(app.Status.ToString(), status.Payload.NewStatus);
    }

    [Fact]
    public void Rows_AreKeyedByApplicationId()
    {
        var app = NewApplication();

        Assert.Equal(app.Id, ApplicationOutboxEvents.Submitted(app, "T").AggregateId);
        Assert.Equal(app.Id, ApplicationOutboxEvents.StatusChanged(app, "Pending", Guid.NewGuid(), "T").AggregateId);
    }
}
