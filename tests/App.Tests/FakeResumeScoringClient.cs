using App.Core.Interfaces;

namespace App.Tests;

/// <summary>
/// Fake ai-svc scoring client. Returns <see cref="Score"/> unless <see cref="FailWith"/> is set, and records
/// every call with the CV bytes it was given.
/// </summary>
public sealed class FakeResumeScoringClient : IResumeScoringClient
{
    public bool Enabled { get; set; } = true;

    public double Score { get; set; } = 72;

    /// <summary>Exception thrown by every call while set (a <see cref="ResumeScoringThrottledException"/> or <see cref="ResumeScoringException"/>).</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Runs inside the call, before it returns. Used to simulate another worker cycle while a job is leased.</summary>
    public Func<Task>? OnCall { get; set; }

    public List<(Guid JobId, string FileName, byte[] CvBytes)> Calls { get; } = [];

    public int CallCount => Calls.Count;

    public async Task<double> ScoreAsync(Stream cvStream, string cvFileName, Guid jobId, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await cvStream.CopyToAsync(buffer, cancellationToken);
        Calls.Add((jobId, cvFileName, buffer.ToArray()));
        if (OnCall is not null)
            await OnCall();

        if (FailWith is not null)
            throw FailWith;
        return Score;
    }
}
