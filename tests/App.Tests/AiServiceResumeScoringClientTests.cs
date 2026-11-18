using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using App.Api.Services;
using App.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace App.Tests;

/// <summary>
/// The ai-svc request and response mapping: route, multipart fields, the bearer service token, and the
/// mapping of each response to a score, a throttle, or a counted failure.
/// </summary>
public class AiServiceResumeScoringClientTests
{
    // Opaque token, deliberately not JWT-shaped (no dots), so a JWT-minting regression cannot pass by accident.
    private const string Token = "scoring-test-token-0123456789abcdefgh";
    private static readonly Regex JwtShape = new(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]*$");
    private static readonly Guid JobId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await respond(request);
        }
    }

    private static IConfiguration Config(string? token = Token, string? url = "http://ai:6000") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI_SERVICE_URL"] = url,
                ["AI_SCORING_SERVICE_TOKEN"] = token,
            })
            .Build();

    private static AiServiceResumeScoringClient Client(StubHandler handler, IConfiguration? config = null) =>
        new(new HttpClient(handler), config ?? Config());

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<ResumeScoreResult> ScoreAsync(AiServiceResumeScoringClient client)
    {
        using var cv = new MemoryStream("%PDF-1.4 cv"u8.ToArray());
        return await client.ScoreAsync(cv, "cv.pdf", JobId);
    }

    [Fact]
    public async Task Score_PostsCvAndJobIdToTheScoreRoute_AndReturnsOverallScore()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"overall_score":81,"band":"good","score_id":"x"}""")));
        var client = Client(handler);

        var result = await ScoreAsync(client);

        Assert.Equal(81, result.Score);
        // The whole response is kept: the recruiter screen reads the breakdown from it.
        Assert.Contains("\"band\":\"good\"", result.DetailJson);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("http://ai:6000/api/ai/score-resume", handler.Request.RequestUri!.ToString());
        Assert.Contains("name=cv_file", handler.RequestBody);
        Assert.Contains("filename=cv.pdf", handler.RequestBody);
        Assert.Contains("name=job_id", handler.RequestBody);
        Assert.Contains(JobId.ToString(), handler.RequestBody);
        Assert.DoesNotContain("lang", handler.RequestBody);
    }

    [Fact]
    public async Task Score_SendsTheConfiguredTokenAsABearer_NotAJwt()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"overall_score":10}""")));
        await ScoreAsync(Client(handler));

        var auth = handler.Request!.Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal(Token, auth.Parameter);
        Assert.DoesNotMatch(JwtShape, auth.Parameter!);
    }

    [Fact]
    public async Task Response429_IsThrottled()
    {
        var client = Client(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.TooManyRequests, """{"code":"rate_limited"}"""))));

        await Assert.ThrowsAsync<ResumeScoringThrottledException>(() => ScoreAsync(client));
    }

    [Fact]
    public async Task Response503Busy_IsThrottled()
    {
        var client = Client(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, """{"code":"busy"}"""))));

        await Assert.ThrowsAsync<ResumeScoringThrottledException>(() => ScoreAsync(client));
    }

    [Fact]
    public async Task Response503JobServiceDown_IsACountedFailure()
    {
        var client = Client(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, """{"code":"job_service_unavailable"}"""))));

        var ex = await Assert.ThrowsAsync<ResumeScoringException>(() => ScoreAsync(client));
        Assert.Contains("job_service_unavailable", ex.Message);
    }

    [Fact]
    public async Task Response404JobNotFound_IsACountedFailure()
    {
        var client = Client(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.NotFound, """{"code":"job_not_found"}"""))));

        await Assert.ThrowsAsync<ResumeScoringException>(() => ScoreAsync(client));
    }

    [Theory]
    [InlineData("""{"band":"good"}""")]
    [InlineData("""{"overall_score":"81"}""")]
    [InlineData("""{"overall_score":101}""")]
    [InlineData("""{"overall_score":-1}""")]
    public async Task Response200WithoutAValidScore_IsACountedFailure(string body)
    {
        var client = Client(new StubHandler(_ => Task.FromResult(Json(HttpStatusCode.OK, body))));

        await Assert.ThrowsAsync<ResumeScoringException>(() => ScoreAsync(client));
    }

    [Fact]
    public async Task UnreachableService_IsACountedFailure()
    {
        var client = Client(new StubHandler(_ => throw new HttpRequestException("connection refused")));

        await Assert.ThrowsAsync<ResumeScoringException>(() => ScoreAsync(client));
    }

    [Fact]
    public async Task Timeout_IsACountedFailure()
    {
        var client = Client(new StubHandler(_ => throw new TaskCanceledException("timeout")));

        var ex = await Assert.ThrowsAsync<ResumeScoringException>(() => ScoreAsync(client));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public void Enabled_RequiresUrlAndToken()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException());
        Assert.True(Client(handler, Config()).Enabled);
        Assert.False(Client(handler, Config(url: "")).Enabled);
        Assert.False(Client(handler, Config(token: "")).Enabled);
        Assert.False(Client(handler, Config(token: "   ")).Enabled);
    }

    [Fact]
    public async Task ScoreWhenNotEnabled_ThrowsWithoutCallingTheService()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not be called"));
        var client = Client(handler, Config(token: ""));

        await Assert.ThrowsAsync<ResumeScoringException>(() => ScoreAsync(client));
        Assert.Null(handler.Request);
    }
}
