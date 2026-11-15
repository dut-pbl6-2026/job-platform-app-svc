using System.Net;
using System.Text;
using App.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace App.Tests;

/// <summary>
/// JobServiceClient reads job data from job-svc's internal endpoint. Apply and status changes depend
/// on it: any failure must surface as a non-Found status, never as assumed job data.
/// </summary>
public class JobServiceClientTests
{
    private const string Token = "test-internal-token";

    [Fact]
    public async Task GetJobAsync_ReturnsCompanyStatusAndTitle_FromInternalEndpoint()
    {
        var jobId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        Uri? requestedUri = null;
        string? authorization = null;
        var handler = new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"companyId\":\"{companyId}\",\"status\":\"Pending\",\"title\":\"Senior .NET Dev\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = BuildClient(handler, "http://job-svc-test:5002/", Token);

        var (status, job) = await client.GetJobAsync(jobId);

        Assert.Equal(JobLookupStatus.Found, status);
        Assert.Equal(new JobDetails(companyId, JobStatus.Pending, "Senior .NET Dev"), job);
        Assert.Equal($"/api/internal/jobs/{jobId}", requestedUri!.AbsolutePath);
        Assert.Equal($"Bearer {Token}", authorization);
    }

    [Fact]
    public async Task GetJobAsync_Unavailable_WhenUrlNotConfigured()
    {
        var called = false;
        var handler = new StubHandler(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = BuildClient(handler, null, Token);

        var (status, job) = await client.GetJobAsync(Guid.NewGuid());

        Assert.Equal(JobLookupStatus.Unavailable, status);
        Assert.Null(job);
        Assert.False(called);
    }

    [Fact]
    public async Task GetJobAsync_Unavailable_WhenTokenNotConfigured()
    {
        var called = false;
        var handler = new StubHandler(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = BuildClient(handler, "http://job-svc-test:5002", null);

        var (status, _) = await client.GetJobAsync(Guid.NewGuid());

        Assert.Equal(JobLookupStatus.Unavailable, status);
        Assert.False(called);
    }

    [Fact]
    public async Task GetJobAsync_NotFound_WhenJobMissing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = BuildClient(handler, "http://job-svc-test:5002", Token);

        var (status, job) = await client.GetJobAsync(Guid.NewGuid());

        Assert.Equal(JobLookupStatus.NotFound, status);
        Assert.Null(job);
    }

    [Fact]
    public async Task GetJobAsync_Unavailable_WhenServerError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = BuildClient(handler, "http://job-svc-test:5002", Token);

        var (status, _) = await client.GetJobAsync(Guid.NewGuid());

        Assert.Equal(JobLookupStatus.Unavailable, status);
    }

    [Fact]
    public async Task GetJobAsync_Unavailable_WhenTransportFails()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler, "http://job-svc-test:5002", Token);

        var (status, _) = await client.GetJobAsync(Guid.NewGuid());

        Assert.Equal(JobLookupStatus.Unavailable, status);
    }

    [Fact]
    public async Task GetJobAsync_Unavailable_WhenStatusUnknown()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"companyId\":\"{Guid.NewGuid()}\",\"status\":\"Archived\",\"title\":\"x\"}}",
                Encoding.UTF8, "application/json"),
        });
        var client = BuildClient(handler, "http://job-svc-test:5002", Token);

        var (status, _) = await client.GetJobAsync(Guid.NewGuid());

        Assert.Equal(JobLookupStatus.Unavailable, status);
    }

    [Fact]
    public async Task GetJobAsync_Unavailable_WhenBodyIncomplete()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"title\":\"x\"}", Encoding.UTF8, "application/json"),
        });
        var client = BuildClient(handler, "http://job-svc-test:5002", Token);

        var (status, _) = await client.GetJobAsync(Guid.NewGuid());

        Assert.Equal(JobLookupStatus.Unavailable, status);
    }

    private static JobServiceClient BuildClient(HttpMessageHandler handler, string? baseUrl, string? token)
    {
        var values = new Dictionary<string, string?> { ["JOB_SERVICE_URL"] = baseUrl };
        if (token is not null)
            values["JOB_INTERNAL_TOKEN_FOR_APP"] = token;
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new JobServiceClient(
            new HttpClient(handler),
            config,
            NullLogger<JobServiceClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
