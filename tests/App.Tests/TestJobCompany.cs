using System.Net;
using System.Security.Claims;
using System.Text;
using App.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace App.Tests;

/// <summary>
/// Fake job-svc for ownership checks. Every job id resolves to <see cref="CompanyId"/>
/// unless a test builds a client with a different responder.
/// </summary>
internal static class TestJobCompany
{
    public static readonly Guid CompanyId = Guid.Parse("c0a0c0a0-0000-4000-8000-00000000000a");
    public static readonly Guid OtherCompanyId = Guid.Parse("c0b0c0b0-0000-4000-8000-00000000000b");

    /// <summary>Client that answers every job lookup with <see cref="CompanyId"/>.</summary>
    public static JobServiceClient Owned() => Create(_ => Json(HttpStatusCode.OK, CompanyId));

    /// <summary>Client that answers every job lookup with <paramref name="companyId"/>.</summary>
    public static JobServiceClient ForCompany(Guid companyId) => Create(_ => Json(HttpStatusCode.OK, companyId));

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

    private static HttpResponseMessage Json(HttpStatusCode status, Guid companyId) =>
        new(status)
        {
            Content = new StringContent($"{{\"id\":\"00000000-0000-0000-0000-000000000001\",\"companyId\":\"{companyId}\"}}",
                Encoding.UTF8, "application/json"),
        };

    private static JobServiceClient Create(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeJobHandler(responder);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JOB_SERVICE_URL"] = "http://job.test" })
            .Build();
        return new JobServiceClient(new HttpClient(handler), config, NullLogger<JobServiceClient>.Instance);
    }

    private sealed class FakeJobHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
