using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace App.Tests;

/// <summary>
/// /metrics backs the Prometheus scrape target (infra/docker/prometheus/prometheus.yml).
/// Asserts the instrumentation is actually wired up, not just that the route exists.
/// </summary>
public class MetricsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public MetricsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetMetrics_ReturnsPrometheusTextWithRuntimeAndHttpMetrics()
    {
        // Arrange: one request so ASP.NET Core metrics have a sample to export.
        await _client.GetAsync("/health");

        // Act
        var response = await _client.GetAsync("/metrics");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/plain", response.Content.Headers.ContentType!.ToString());

        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("dotnet_gc_last_collection_heap_size_bytes", content);
        Assert.Contains("http_server_request_duration_seconds_count", content);
    }
}
