using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using App.Core.Interfaces;

namespace App.Api.Services;

/// <summary>
/// Calls ai-svc <c>POST /api/ai/score-resume</c> (multipart: <c>cv_file</c>, <c>job_id</c>) and returns
/// <c>overall_score</c>. ai-svc loads the job description itself from job-svc by <c>job_id</c>, so no job text
/// is sent from here.
///
/// ai-svc authenticates the caller with its per-caller service token: the worker sends
/// <c>Authorization: Bearer &lt;AI_SCORING_SERVICE_TOKEN&gt;</c>. ai-svc accepts that token on this route only.
/// Scoring is rate limited per caller in ai-svc, so all automatic scoring shares that limit.
///
/// Enabled only when AI_SERVICE_URL and AI_SCORING_SERVICE_TOKEN are both configured (non-blank).
/// </summary>
public sealed class AiServiceResumeScoringClient : IResumeScoringClient
{
    /// <summary>Minimum length of AI_SCORING_SERVICE_TOKEN. Enforced at startup in Production.</summary>
    public const int MinTokenLength = 32;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _token;

    public AiServiceResumeScoringClient(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _baseUrl = (configuration["AI_SERVICE_URL"] ?? configuration["AiService:Url"] ?? "").Trim().TrimEnd('/');
        _token = (configuration["AI_SCORING_SERVICE_TOKEN"] ?? "").Trim();
    }

    /// <inheritdoc />
    public bool Enabled => !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_token);

    /// <inheritdoc />
    public async Task<double> ScoreAsync(Stream cvStream, string cvFileName, Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
            throw new ResumeScoringException("AI service is not configured.");

        using var content = new MultipartFormDataContent();
        using var cvContent = new StreamContent(cvStream);
        cvContent.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(cvFileName));
        content.Add(cvContent, "cv_file", cvFileName);
        content.Add(new StringContent(jobId.ToString()), "job_id");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/ai/score-resume")
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ResumeScoringThrottledException("AI service rate limit reached (HTTP 429).");

            if (!response.IsSuccessStatusCode)
            {
                var code = await ReadErrorCodeAsync(response, cancellationToken);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && code == "busy")
                    throw new ResumeScoringThrottledException("AI service is busy (HTTP 503 busy).");

                throw new ResumeScoringException(
                    $"AI service returned HTTP {(int)response.StatusCode} ({code ?? "no error code"}).");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("overall_score", out var scoreElement)
                || scoreElement.ValueKind != JsonValueKind.Number
                || !scoreElement.TryGetDouble(out var score)
                || double.IsNaN(score) || score < 0 || score > 100)
            {
                throw new ResumeScoringException("AI service response has no valid overall_score.");
            }

            return score;
        }
        catch (HttpRequestException ex)
        {
            throw new ResumeScoringException("AI service is unreachable.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ResumeScoringException("AI service call timed out.", ex);
        }
    }

    /// <summary>Reads the RFC 7807 <c>code</c> of an error response. The body is never logged.</summary>
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream",
        };
}
