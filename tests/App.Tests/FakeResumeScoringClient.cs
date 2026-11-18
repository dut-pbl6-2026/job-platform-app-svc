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

    /// <summary>Body the fake reports as the scoring detail; mirrors ai-svc's response shape.</summary>
    public string DetailJson { get; set; } =
        """{"overall_score":72,"band":"Good","breakdown":{"skills":{"score":80,"matching":["c#"],"missing":[],"negated":[],"extra":[],"evidence":{}},"semantic":{"score":70,"title_fit":0.8,"top_matches":[]},"experience":{"score":100,"cv_years":4,"required_years":3},"education":{"score":100,"cv_level":"bachelor","required_level":"bachelor"}},"suggestions":[],"warnings":[],"meta":{"parser":"text","pages":1,"language":"en","model":"test","cached":false,"elapsed_ms":1}}""";

    /// <summary>Exception thrown by every call while set (a <see cref="ResumeScoringThrottledException"/> or <see cref="ResumeScoringException"/>).</summary>
    public Exception? FailWith { get; set; }

    /// <summary>Runs inside the call, before it returns. Used to simulate another worker cycle while a job is leased.</summary>
    public Func<Task>? OnCall { get; set; }

    public List<(Guid JobId, string FileName, byte[] CvBytes)> Calls { get; } = [];

    public int CallCount => Calls.Count;

    public async Task<ResumeScoreResult> ScoreAsync(Stream cvStream, string cvFileName, Guid jobId, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await cvStream.CopyToAsync(buffer, cancellationToken);
        Calls.Add((jobId, cvFileName, buffer.ToArray()));
        if (OnCall is not null)
            await OnCall();

        if (FailWith is not null)
            throw FailWith;
        return new ResumeScoreResult(Score, DetailJson);
    }
}
