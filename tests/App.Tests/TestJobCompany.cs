using System.Net;
using System.Security.Claims;
using System.Text;
using App.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace App.Tests;

/// <summary>
/// Fake job-svc internal lookup. Every job id resolves to <see cref="CompanyId"/>, an Active job
/// and <see cref="Title"/>, unless a test builds a client with a different responder.
/// </summary>
internal static class TestJobCompany
{
    public static readonly Guid CompanyId = Guid.Parse("c0a0c0a0-0000-4000-8000-00000000000a");
    public static readonly Guid OtherCompanyId = Guid.Parse("c0b0c0b0-0000-4000-8000-00000000000b");
    public const string Title = "Senior .NET Developer";

    /// <summary>Client that answers every job lookup with <see cref="CompanyId"/> and an Active job.</summary>
    public static JobServiceClient Owned() => Create(_ => Json(HttpStatusCode.OK, CompanyId));

    /// <summary>Client that answers every job lookup with <paramref name="companyId"/> and an Active job.</summary>
    public static JobServiceClient ForCompany(Guid companyId) => Create(_ => Json(HttpStatusCode.OK, companyId));

    /// <summary>Client that answers every job lookup with a job in <paramref name="jobStatus"/>.</summary>
    public static JobServiceClient WithJobStatus(string jobStatus) =>
        Create(_ => Json(HttpStatusCode.OK, CompanyId, jobStatus));

    /// <summary>Raw job-svc response for an Active job owned by <see cref="CompanyId"/>, for responders that wrap it.</summary>
    public static HttpResponseMessage OwnedJobResponse(string jobStatus = "Active") =>
        Json(HttpStatusCode.OK, CompanyId, jobStatus);

    /// <summary>Client built from a custom responder, for tests that intercept job-svc calls.</summary>
    public static JobServiceClient WithResponder(Func<HttpRequestMessage, HttpResponseMessage> responder) => Create(responder);

    /// <summary>Client whose job-svc responds with a server error, to test fail-closed behaviour.</summary>
    public static JobServiceClient Down() => Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

    /// <summary>Client that reports the job as missing.</summary>
    public static JobServiceClient Missing() => Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

    /// <summary>Recruiter identity linked to <paramref name="companyId"/>, or unlinked when null.</summary>
    public static HttpContext RecruiterContext(Guid userId, Guid? companyId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, "Recruiter"),
        };
        if (companyId.HasValue)
            claims.Add(new Claim("company_id", companyId.Value.ToString()));

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        ctx.Request.Headers["X-User-Id"] = userId.ToString();
        ctx.Request.Headers["X-User-Role"] = "Recruiter";
        return ctx;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, Guid companyId, string jobStatus = "Active") =>
        new(status)
        {
            Content = new StringContent(
                $"{{\"companyId\":\"{companyId}\",\"status\":\"{jobStatus}\",\"title\":\"{Title}\"}}",
                Encoding.UTF8, "application/json"),
        };

    /// <summary>Owned-by-<see cref="CompanyId"/> client that has NO internal token configured.</summary>
    public static JobServiceClient NoInternalToken() => Create(_ => Json(HttpStatusCode.OK, CompanyId), token: null);

    /// <summary>Client that records the Authorization header of the last request it received.</summary>
    public static (JobServiceClient Client, Func<string?> LastAuthorization) Recording()
    {
        string? last = null;
        var client = Create(req =>
        {
            last = req.Headers.Authorization?.ToString();
            return Json(HttpStatusCode.OK, CompanyId);
        });
        return (client, () => last);
    }

    /// <summary>Client that records the path of every request it receives, for asserting which job-svc endpoints are called.</summary>
    public static (JobServiceClient Client, List<string> Paths) RecordingPaths()
    {
        var paths = new List<string>();
        var client = Create(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return Json(HttpStatusCode.OK, CompanyId);
        });
        return (client, paths);
    }

    private static JobServiceClient Create(Func<HttpRequestMessage, HttpResponseMessage> responder, string? token = "test-internal-token")
    {
        var handler = new FakeJobHandler(responder);
        var values = new Dictionary<string, string?> { ["JOB_SERVICE_URL"] = "http://job.test" };
        if (token is not null)
            values["JOB_INTERNAL_TOKEN_FOR_APP"] = token;
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new JobServiceClient(new HttpClient(handler), config, NullLogger<JobServiceClient>.Instance);
    }

    private sealed class FakeJobHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
