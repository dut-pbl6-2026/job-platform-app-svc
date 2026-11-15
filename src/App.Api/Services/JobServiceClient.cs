using System.Net.Http.Json;
using System.Text.Json;

namespace App.Api.Services;

/// <summary>
/// HTTP client to read job data from job-svc. App-svc owns only the application database, so the
/// owning company, status and title of a job must be fetched over HTTP.
///
/// Contract: GET {JOB_SERVICE_URL}/api/internal/jobs/{id} with the JOB_INTERNAL_TOKEN_FOR_APP token ->
/// <c>{ "companyId": "...", "status": "Active", "title": "..." }</c>.
/// Uses the internal endpoint only: the public GET /api/jobs/{id} counts a view on every call.
/// </summary>
public class JobServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _internalToken;
    private readonly ILogger<JobServiceClient> _logger;

    public JobServiceClient(HttpClient http, IConfiguration configuration, ILogger<JobServiceClient> logger)
    {
        _http = http;
        _baseUrl = (configuration["JOB_SERVICE_URL"] ?? configuration["JobService:Url"] ?? "").Trim().TrimEnd('/');
        _internalToken = (configuration["JOB_INTERNAL_TOKEN_FOR_APP"] ?? "").Trim();
        _logger = logger;
    }

    /// <summary>False when JOB_SERVICE_URL is not configured; lookups are skipped.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_baseUrl);

    /// <summary>
    /// Resolves the job's owning company, status and title. Callers must treat anything other than
    /// <see cref="JobLookupStatus.Found"/> as a failure: no job data may be assumed.
    /// </summary>
    public async Task<(JobLookupStatus Status, JobDetails? Job)> GetJobAsync(Guid jobId, CancellationToken ct = default)
    {
        // Fail closed: no job-svc URL or no internal token means no job data can be proven.
        if (!Enabled || string.IsNullOrEmpty(_internalToken))
        {
            return (JobLookupStatus.Unavailable, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/internal/jobs/{jobId}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _internalToken);
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (JobLookupStatus.NotFound, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Job lookup HTTP {Status} for JobId={JobId}.", (int)response.StatusCode, jobId);
                return (JobLookupStatus.Unavailable, null);
            }

            var payload = await response.Content.ReadFromJsonAsync<JobResponse>(JsonOptions, ct);
            if (payload is null || payload.CompanyId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Status))
            {
                _logger.LogWarning("Job lookup returned an incomplete body for JobId={JobId}.", jobId);
                return (JobLookupStatus.Unavailable, null);
            }

            if (!Enum.TryParse<JobStatus>(payload.Status, ignoreCase: false, out var status))
            {
                _logger.LogWarning("Job lookup returned unknown status {Status} for JobId={JobId}.", payload.Status, jobId);
                return (JobLookupStatus.Unavailable, null);
            }

            return (JobLookupStatus.Found, new JobDetails(payload.CompanyId, status, payload.Title ?? string.Empty));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job lookup failed for JobId={JobId}.", jobId);
            return (JobLookupStatus.Unavailable, null);
        }
    }

    private sealed record JobResponse(Guid CompanyId, string? Status, string? Title);
}

public enum JobLookupStatus
{
    Found,
    NotFound,
    Unavailable,
}

/// <summary>Job data as known to job-svc. <see cref="Status"/> mirrors job-svc's JobStatus names.</summary>
public sealed record JobDetails(Guid CompanyId, JobStatus Status, string Title);

public enum JobStatus
{
    Active,
    Closed,
    Deleted,
    Pending,
    Rejected,
}
