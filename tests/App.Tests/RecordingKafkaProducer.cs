using SharedKernel.Events;
using SharedKernel.Kafka;

namespace App.Tests;

/// <summary>
/// Fake Kafka producer. Records every acknowledged send, counts every attempt, and fails the next
/// <see cref="FailuresRemaining"/> attempts with <see cref="FailWith"/>.
/// </summary>
internal sealed class RecordingKafkaProducer : IKafkaProducer
{
    public List<(string Topic, string Key, object Envelope)> Sent { get; } = new();

    public List<string> SentEventTypes { get; } = new();

    public int Attempts { get; private set; }

    public int FailuresRemaining { get; set; }

    public Exception FailWith { get; set; } = new InvalidOperationException("broker unavailable");

    public Task ProduceAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default)
    {
        Attempts++;
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw FailWith;
        }

        Sent.Add((topic, key, envelope!));
        SentEventTypes.Add(envelope.EventType);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}
