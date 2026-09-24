using SharedKernel.Kafka;

namespace App.Api.Services;

/// <summary>
/// No-op <see cref="IKafkaProducer"/> used when Kafka is not configured
/// (KAFKA_BOOTSTRAP_SERVERS empty). Keeps application CRUD working locally.
/// </summary>
public sealed class NullKafkaProducer : IKafkaProducer
{
    private readonly ILogger<NullKafkaProducer> _logger;

    public NullKafkaProducer(ILogger<NullKafkaProducer> logger)
    {
        _logger = logger;
    }

    public Task ProduceAsync<T>(string topic, string key, SharedKernel.Events.EventEnvelope<T> envelope, CancellationToken ct = default)
    {
        _logger.LogDebug("Kafka disabled. Skipping {EventType} key={Key} topic={Topic}.", envelope.EventType, key, topic);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
