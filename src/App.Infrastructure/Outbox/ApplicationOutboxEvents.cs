using System.Text.Json;
using App.Core.Entities;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace App.Infrastructure.Outbox;

/// <summary>
/// Builds the outbox rows for application.submitted and application.status_changed. The caller adds the
/// row to the same <c>SaveChanges</c> as the application change. The envelope is serialized with
/// <see cref="KafkaJson.Options"/>, the same policy the Kafka producer uses.
/// </summary>
public static class ApplicationOutboxEvents
{
    public static OutboxEvent Submitted(Application application, string jobTitle)
    {
        var payload = new ApplicationSubmittedEvent(
            application.Id, application.JobId, jobTitle ?? "",
            application.ApplicantId, DateTime.UtcNow);
        return Build(ApplicationEventTypes.Submitted, payload, application.Id);
    }

    public static OutboxEvent StatusChanged(
        Application application, string previousStatus, Guid changedBy, string jobTitle)
    {
        var payload = new ApplicationStatusChangedEvent(
            application.Id, application.JobId, jobTitle ?? "",
            application.ApplicantId, previousStatus,
            application.Status.ToString(), changedBy, DateTime.UtcNow);
        return Build(ApplicationEventTypes.StatusChanged, payload, application.Id);
    }

    private static OutboxEvent Build<T>(string eventType, T payload, Guid applicationId) =>
        new(
            eventType,
            JsonSerializer.Serialize(EventEnvelope<T>.Create(eventType, payload), KafkaJson.Options),
            applicationId);
}
