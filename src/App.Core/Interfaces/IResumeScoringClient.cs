namespace App.Core.Interfaces;

/// <summary>
/// Scores a CV against the job it was submitted for. The implementation calls the ai-svc resume scoring
/// endpoint; no third-party AI API is ever called.
/// </summary>
public interface IResumeScoringClient
{
    /// <summary>False when the ai-svc URL or the JWT settings are not configured. Nothing is claimed then.</summary>
    bool Enabled { get; }

    /// <summary>Returns the match score from 0 to 100.</summary>
    /// <exception cref="ResumeScoringThrottledException">The service is rate limiting or busy. Retry later; not a failed attempt.</exception>
    /// <exception cref="ResumeScoringException">Any other failure, including timeouts. Counts as a failed attempt.</exception>
    Task<double> ScoreAsync(Stream cvStream, string cvFileName, Guid jobId, CancellationToken cancellationToken = default);
}

/// <summary>The scoring service refused the call for now (rate limit or busy). The attempt is not counted.</summary>
public sealed class ResumeScoringThrottledException(string message) : Exception(message);

/// <summary>The scoring call failed. The attempt is counted.</summary>
public sealed class ResumeScoringException(string message, Exception? inner = null) : Exception(message, inner);
