using System.Net.Http.Json;
using System.Text.Json;

namespace App.Api.Services;

/// <summary>
/// HTTP client to resolve denormalized job metadata from job-svc (PBL6-34 / B-10).
/// App-svc owns only the application database, so <c>JobTitle</c> for application
/// events must be fetched over HTTP before publishing.
///
/// Contract: GET {JOB_SERVICE_URL}/api/jobs/{id} -> <c>{ "title": "..." }</c>.
/// Best-effort: never throws; a disabled client, a non-success status or any
/// transport error returns an empty title and logs a warning. The notif-svc
/// template renders an empty title as "[unknown]".
/// </summary>
public class JobServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly ILogger<JobServiceClient> _logger;

    public JobServiceClient(HttpClient http, IConfiguration configuration, ILogger<JobServiceClient> logger)
    {
        _http = http;
        _baseUrl = (configuration["JOB_SERVICE_URL"] ?? configuration["JobService:Url"] ?? "").Trim().TrimEnd('/');
        _logger = logger;
    }

    /// <summary>False when JOB_SERVICE_URL is not configured; lookups are skipped.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_baseUrl);

    /// <summary>
    /// Returns the job title or an empty string when it cannot be resolved.
    /// Never throws, so it cannot break the application write path.
    /// </summary>
    public async Task<string> GetJobTitleAsync(Guid jobId, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return string.Empty;
        }

        try
        {
            using var response = await _http.GetAsync($"{_baseUrl}/api/jobs/{jobId}", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "JobTitle lookup HTTP {Status} for JobId={JobId} at {Url}. Publishing with empty title.",
                    (int)response.StatusCode, jobId, _baseUrl);
                return string.Empty;
            }

            var payload = await response.Content.ReadFromJsonAsync<JobTitleResponse>(JsonOptions, ct);
            return payload?.Title ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "JobTitle lookup failed (non-blocking) for JobId={JobId} at {Url}. Publishing with empty title.",
                jobId, _baseUrl);
            return string.Empty;
        }
    }

    private sealed record JobTitleResponse(string? Title);
}
