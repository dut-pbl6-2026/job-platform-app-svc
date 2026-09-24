using SharedKernel.Events;
using SharedKernel.Kafka;
using App.Core.Entities;

namespace App.Api.Services;

/// <summary>
/// Publishes application lifecycle events to the <c>application-events</c> Kafka topic
/// (PBL6-34, SRS KAFKA-01-02 / KAFKA-01-04).
/// Best-effort: never fails application CRUD and never propagates exceptions.
/// Kafka key is always the ApplicationId so one application stays ordered.
/// NOTE (PBL6-35): JobTitle is denormalized for the email template. App-svc has no
/// job read model yet, so callers pass what they know (empty = consumer resolves).
/// </summary>
public class ApplicationEventPublisher
{
    private readonly IKafkaProducer _producer;
    private readonly string _topic;
    private readonly ILogger<ApplicationEventPublisher> _logger;

    public ApplicationEventPublisher(IKafkaProducer producer, IConfiguration configuration, ILogger<ApplicationEventPublisher> logger)
    {
        _producer = producer;
        _topic = (configuration["KAFKA_TOPIC_APPLICATION_EVENTS"]
            ?? configuration["Kafka:Topic"]
            ?? "application-events").Trim();
        if (string.IsNullOrWhiteSpace(_topic))
        {
            _topic = "application-events";
        }

        _logger = logger;
    }

    public async Task PublishSubmittedAsync(Application application, string jobTitle = "")
    {
        var payload = new ApplicationSubmittedEvent(
            application.Id, application.JobId, jobTitle ?? "",
            application.ApplicantId, DateTime.UtcNow);
        await ProduceAsync(
            ApplicationEventTypes.Submitted,
            application.Id.ToString(),
            EventEnvelope<ApplicationSubmittedEvent>.Create(ApplicationEventTypes.Submitted, payload));
    }

    public async Task PublishStatusChangedAsync(Application application, string previousStatus, Guid changedBy, string jobTitle = "")
    {
        var payload = new ApplicationStatusChangedEvent(
            application.Id, application.JobId, jobTitle ?? "",
            application.ApplicantId, previousStatus,
            application.Status.ToString(), changedBy, DateTime.UtcNow);
        await ProduceAsync(
            ApplicationEventTypes.StatusChanged,
            application.Id.ToString(),
            EventEnvelope<ApplicationStatusChangedEvent>.Create(ApplicationEventTypes.StatusChanged, payload));
    }

    private async Task ProduceAsync<T>(string eventType, string key, EventEnvelope<T> envelope)
    {
        try
        {
            await _producer.ProduceAsync(_topic, key, envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka publish failed (non-blocking) {EventType} key={Key} topic={Topic}.", eventType, key, _topic);
        }
    }
}
