using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Entities;
using App.Core.Interfaces;
using App.Infrastructure.Data;
using App.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace App.Tests;

/// <summary>
/// HTTP-level scoring flow through the real pipeline: applying queues a pending scoring job, a worker cycle
/// scores it with a fake ai-svc, recruiters see the score and its status, and applicants never see either.
/// </summary>
public class ApplicationScoringIntegrationTests : IDisposable
{
    private readonly List<ApplicationApiFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private ApplicationApiFactory NewFactory()
    {
        var f = new ApplicationApiFactory();
        _factories.Add(f);
        return f;
    }

    private static HttpClient As(ApplicationApiFactory f, Guid userId, string role, bool linkedToOwnedCompany = false)
    {
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", role);
        if (linkedToOwnedCompany)
            client.DefaultRequestHeaders.Add("X-User-Company", TestJobCompany.CompanyId.ToString());
        return client;
    }

    private static MultipartFormDataContent ApplyForm(Guid jobId)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(jobId.ToString()), "job_id" },
            { new StringContent("I want this job"), "cover_letter" },
        };
        var file = new ByteArrayContent("%PDF-1.4 scoring http test"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "cv_file", "cv.pdf");
        return form;
    }

    /// <summary>Applies as a new applicant and returns the application id.</summary>
    private static async Task<(Guid ApplicationId, Guid ApplicantId)> ApplyAsync(ApplicationApiFactory f, Guid jobId)
    {
        var applicantId = Guid.NewGuid();
        var response = await As(f, applicantId, "User").PostAsync("/api/applications", ApplyForm(jobId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("id").GetGuid(), applicantId);
    }

    private static async Task RunWorkerCycleAsync(ApplicationApiFactory f)
    {
        var worker = f.Services.GetRequiredService<ResumeScoringWorkerService>();
        await worker.ScorePendingAsync(CancellationToken.None);
    }

    private static async Task MakeDueAsync(ApplicationApiFactory f, Guid applicationId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ScoringJobs.SingleAsync(s => s.ApplicationId == applicationId);
        job.Release(DateTime.UtcNow.AddSeconds(-1));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Apply_CreatesOnePendingScoringJobInTheSameWriteAsTheApplication()
    {
        var f = NewFactory();
        var jobId = Guid.NewGuid();

        var (applicationId, _) = await ApplyAsync(f, jobId);

        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ScoringJobs.SingleAsync();
        Assert.Equal(applicationId, job.ApplicationId);
        Assert.Equal(ScoringStatus.Pending, job.Status);
        Assert.Equal(0, job.AttemptCount);
        Assert.Null(job.Score);
    }

    [Fact]
    public async Task Apply_RejectedDuplicate_AddsNoSecondScoringJob()
    {
        var f = NewFactory();
        var jobId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();
        var client = As(f, applicantId, "User");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync("/api/applications", ApplyForm(jobId))).StatusCode);

        var second = await client.PostAsync("/api/applications", ApplyForm(jobId));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var scope = f.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().ScoringJobs.CountAsync());
    }

    [Fact]
    public async Task WorkerScores_RecruiterSeesScoreAndDoneStatus_InDetailAndJobList()
    {
        var f = NewFactory();
        var jobId = Guid.NewGuid();
        var (applicationId, _) = await ApplyAsync(f, jobId);
        f.ScoringClient.Score = 77;

        await RunWorkerCycleAsync(f);

        Assert.Single(f.ScoringClient.Calls);
        Assert.Equal(jobId, f.ScoringClient.Calls[0].JobId);

        var recruiter = As(f, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);
        var detail = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/{applicationId}");
        Assert.Equal(77, detail.GetProperty("score").GetDouble());
        Assert.Equal("done", detail.GetProperty("scoreStatus").GetString());

        var list = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/job/{jobId}");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(77, item.GetProperty("score").GetDouble());
        Assert.Equal("done", item.GetProperty("scoreStatus").GetString());
    }

    [Fact]
    public async Task PutStatusWithScore_IsIgnored_AndTheRecruiterScoreComesOnlyFromTheScoringJob()
    {
        var f = NewFactory();
        var jobId = Guid.NewGuid();
        var (applicationId, _) = await ApplyAsync(f, jobId);
        var recruiter = As(f, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);

        var response = await recruiter.PutAsJsonAsync(
            $"/api/applications/{applicationId}/status",
            new { status = "reviewed", note = "Passed screening", score = 42 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("score", out _));

        // Right after the update the recruiter sees no score yet: the manual 42 was not stored anywhere.
        var before = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/{applicationId}");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("score").ValueKind);
        Assert.Equal("pending", before.GetProperty("scoreStatus").GetString());

        // The automatic score is the only recruiter-visible score.
        f.ScoringClient.Score = 77;
        await RunWorkerCycleAsync(f);
        var after = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/{applicationId}");
        Assert.Equal(77, after.GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task ScoredApplication_ApplicantResponsesHaveNoScoreKeys_RecruiterResponsesDo()
    {
        var f = NewFactory();
        var jobId = Guid.NewGuid();
        var (applicationId, applicantId) = await ApplyAsync(f, jobId);
        f.ScoringClient.Score = 83;
        await RunWorkerCycleAsync(f);

        var applicant = As(f, applicantId, "User");
        var detail = await applicant.GetFromJsonAsync<JsonElement>($"/api/applications/{applicationId}");
        Assert.False(detail.TryGetProperty("score", out _));
        Assert.False(detail.TryGetProperty("scoreStatus", out _));

        var mine = await applicant.GetFromJsonAsync<JsonElement>("/api/applications/me");
        var summary = Assert.Single(mine.GetProperty("items").EnumerateArray());
        Assert.Equal(applicationId, summary.GetProperty("id").GetGuid());
        Assert.False(summary.TryGetProperty("score", out _));
        Assert.False(summary.TryGetProperty("scoreStatus", out _));

        var recruiter = As(f, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);
        var recruiterDetail = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/{applicationId}");
        Assert.Equal(83, recruiterDetail.GetProperty("score").GetDouble());
        Assert.Equal("done", recruiterDetail.GetProperty("scoreStatus").GetString());

        var jobApplications = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/job/{jobId}");
        var recruiterRow = Assert.Single(jobApplications.GetProperty("items").EnumerateArray());
        Assert.Equal(83, recruiterRow.GetProperty("score").GetDouble());
        Assert.Equal("done", recruiterRow.GetProperty("scoreStatus").GetString());
    }

    [Fact]
    public async Task RecruiterSeesPendingStatus_BeforeTheWorkerRuns()
    {
        var f = NewFactory();
        var jobId = Guid.NewGuid();
        var (applicationId, _) = await ApplyAsync(f, jobId);

        var recruiter = As(f, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);
        var detail = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/{applicationId}");

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("score").ValueKind);
        Assert.Equal("pending", detail.GetProperty("scoreStatus").GetString());
    }

    [Fact]
    public async Task FailingAiService_RetriesThenRecruiterSeesFailed()
    {
        var f = NewFactory();
        var jobId = Guid.NewGuid();
        var (applicationId, _) = await ApplyAsync(f, jobId);
        f.ScoringClient.FailWith = new ResumeScoringException("ai-svc returned HTTP 500.");

        for (var attempt = 0; attempt < ScoringRetryPolicy.MaxAttempts; attempt++)
        {
            await MakeDueAsync(f, applicationId);
            await RunWorkerCycleAsync(f);
        }

        var recruiter = As(f, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);
        var detail = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/{applicationId}");
        Assert.Equal("failed", detail.GetProperty("scoreStatus").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("score").ValueKind);
        Assert.Equal(ScoringRetryPolicy.MaxAttempts, f.ScoringClient.CallCount);
    }

    [Fact]
    public async Task ApplicantNeverSeesTheScoreOrItsStatus()
    {
        var f = NewFactory();
        var jobId = Guid.NewGuid();
        var (applicationId, applicantId) = await ApplyAsync(f, jobId);
        var applicant = As(f, applicantId, "User");

        // Before, during and after scoring: the applicant payloads never carry scoring fields.
        var before = await applicant.GetStringAsync($"/api/applications/{applicationId}");
        await RunWorkerCycleAsync(f);
        var detailAfter = await applicant.GetStringAsync($"/api/applications/{applicationId}");
        var mine = await applicant.GetStringAsync("/api/applications/me");

        foreach (var json in new[] { before, detailAfter, mine })
        {
            Assert.DoesNotContain("\"score\"", json);
            Assert.DoesNotContain("scoreStatus", json);
        }

        // The recruiter view of the same application does carry the score.
        var recruiter = As(f, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);
        var recruiterDetail = await recruiter.GetFromJsonAsync<JsonElement>($"/api/applications/{applicationId}");
        Assert.Equal("done", recruiterDetail.GetProperty("scoreStatus").GetString());
    }
}
