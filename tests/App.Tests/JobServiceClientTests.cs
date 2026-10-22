using System.Net;
using System.Text;
using App.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace App.Tests;

/// <summary>
/// PBL6-34 / B-10: JobServiceClient resolves JobTitle over HTTP and must fail soft
/// (empty title) so application writes are never blocked by job-svc.
/// </summary>
public class JobServiceClientTests
{
    [Fact]
    public async Task GetJobTitleAsync_ReturnsTitle_WhenJobServiceRespondsOk()
    {
        var jobId = Guid.NewGuid();
        Uri? requestedUri = null;
        var handler = new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"id\":\"{jobId}\",\"title\":\"Senior .NET Dev\"}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = BuildClient(handler, "http://job-svc-test:5002/");

        var title = await client.GetJobTitleAsync(jobId);

        Assert.Equal("Senior .NET Dev", title);
        Assert.NotNull(requestedUri);
        Assert.Equal($"/api/jobs/{jobId}", requestedUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetJobTitleAsync_ReturnsEmpty_WhenUrlNotConfigured()
    {
        var called = false;
        var handler = new StubHandler(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = BuildClient(handler, null);

        var title = await client.GetJobTitleAsync(Guid.NewGuid());

        Assert.Equal(string.Empty, title);
        Assert.False(called);
    }

    [Fact]
    public async Task GetJobTitleAsync_ReturnsEmpty_WhenJobNotFound()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = BuildClient(handler, "http://job-svc-test:5002");

        var title = await client.GetJobTitleAsync(Guid.NewGuid());

        Assert.Equal(string.Empty, title);
    }

    [Fact]
    public async Task GetJobTitleAsync_ReturnsEmpty_WhenTransportFails()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var client = BuildClient(handler, "http://job-svc-test:5002");

        var title = await client.GetJobTitleAsync(Guid.NewGuid());

        Assert.Equal(string.Empty, title);
    }

    [Fact]
    public async Task GetJobTitleAsync_ReturnsEmpty_WhenTitleMissing()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var client = BuildClient(handler, "http://job-svc-test:5002");

        var title = await client.GetJobTitleAsync(Guid.NewGuid());

        Assert.Equal(string.Empty, title);
    }

    private static JobServiceClient BuildClient(HttpMessageHandler handler, string? baseUrl)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JOB_SERVICE_URL"] = baseUrl })
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
