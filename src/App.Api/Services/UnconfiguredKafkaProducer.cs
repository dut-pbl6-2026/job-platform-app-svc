using SharedKernel.Events;
using SharedKernel.Kafka;

namespace App.Api.Services;

/// <summary>
/// <see cref="IKafkaProducer"/> used when Kafka is not configured (KAFKA_BOOTSTRAP_SERVERS empty).
/// Every produce fails, so outbox rows stay unpublished and are retried with backoff until Kafka is
/// configured. It must not succeed silently: a success would mark the rows published without sending them.
/// </summary>
public sealed class UnconfiguredKafkaProducer : IKafkaProducer
{
    public Task ProduceAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Kafka is not configured (KAFKA_BOOTSTRAP_SERVERS is empty). Cannot publish {envelope.EventType} key={key} topic={topic}.");

    public void Dispose()
    {
    }
}
