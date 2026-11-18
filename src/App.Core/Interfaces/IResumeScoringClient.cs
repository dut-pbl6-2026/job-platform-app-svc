namespace App.Core.Interfaces;

/// <summary>One scoring response: the headline score and the full report ai-svc returned as JSON.</summary>
public sealed record ResumeScoreResult(double Score, string DetailJson);

/// <summary>
/// Scores a CV against the job it was submitted for. The implementation calls the ai-svc resume scoring
/// endpoint; no third-party AI API is ever called.
/// </summary>
public interface IResumeScoringClient
{
    /// <summary>False when the ai-svc URL or the JWT settings are not configured. Nothing is claimed then.</summary>
    bool Enabled { get; }

    /// <summary>
    /// Returns the match score and the whole response body (breakdown, matched skills, suggestions) so the
    /// recruiter screen can show what produced the number instead of a bare figure.
    /// </summary>
    /// <exception cref="ResumeScoringThrottledException">The service is rate limiting or busy. Retry later; not a failed attempt.</exception>
    /// <exception cref="ResumeScoringException">Any other failure, including timeouts. Counts as a failed attempt.</exception>
    Task<ResumeScoreResult> ScoreAsync(Stream cvStream, string cvFileName, Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>The scoring service refused the call for now (rate limit or busy). The attempt is not counted.</summary>
public sealed class ResumeScoringThrottledException(string message) : Exception(message);

/// <summary>The scoring call failed. The attempt is counted.</summary>
public sealed class ResumeScoringException(string message, Exception? inner = null) : Exception(message, inner);
