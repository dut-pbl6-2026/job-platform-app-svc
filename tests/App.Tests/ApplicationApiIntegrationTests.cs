using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using App.Core.Entities;
using App.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace App.Tests;

/// <summary>
/// HTTP-level integration tests through the real pipeline (routing, auth middleware, JSON binding),
/// backed by the in-memory provider and a fake job-svc. Each test owns its factory, so job-svc behaviour
/// and the database are isolated per test.
/// </summary>
public class ApplicationApiIntegrationTests : IDisposable
{
    private readonly List<ApplicationApiFactory> _factories = [];

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private ApplicationApiFactory NewFactory(Func<HttpRequestMessage, HttpResponseMessage>? jobResponder = null)
    {
        var f = new ApplicationApiFactory(jobResponder);
        _factories.Add(f);
        return f;
    }

    private static HttpClient As(WebApplicationFactory<Program> f, Guid userId, string role, bool linkedToOwnedCompany = false)
    {
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", role);
        if (linkedToOwnedCompany)
            client.DefaultRequestHeaders.Add("X-User-Company", TestJobCompany.CompanyId.ToString());
        return client;
    }

    private static async Task<Guid> SeedApplicationAsync(WebApplicationFactory<Program> f, Guid jobId, Guid applicantId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        var app = new Application(jobId, applicantId, "/api/applications/cv/seed.pdf", "cover");
        app.SetRecruiterNotes("Internal note");
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private static MultipartFormDataContent ApplyForm(Guid jobId)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(jobId.ToString()), "job_id" },
            { new StringContent("I want this job"), "cover_letter" },
        };
        var file = new ByteArrayContent("%PDF-1.4 test"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "cv_file", "cv.pdf");
        return form;
    }

    [Fact]
    public async Task ConcurrentStatusUpdates_OneSucceeds_LoserGets409()
    {
        // Both recruiter requests load the application at version 0, then hold at the job-svc ownership
        // lookup (the point between load and save) until both have loaded. Only the first two job-svc calls
        // block; later calls (the job title lookup after commit) pass straight through.
        var jobId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();
        var barrier = new Barrier(2);
        var calls = 0;
        var factory = NewFactory(_ =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
                barrier.SignalAndWait(TimeSpan.FromSeconds(15));
            return TestJobCompany.OwnedJobResponse();
        });
        var appId = await SeedApplicationAsync(factory, jobId, applicantId);

        var recruiterA = As(factory, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);
        var recruiterB = As(factory, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);
        var url = $"/api/applications/{appId}/status";

        var (a, b) = await TaskPair(
            recruiterA.PutAsJsonAsync(url, new { status = "reviewed" }),
            recruiterB.PutAsJsonAsync(url, new { status = "rejected" }));

        var codes = new[] { a.StatusCode, b.StatusCode }.OrderBy(c => (int)c).ToArray();
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, codes);

        var winner = a.StatusCode == HttpStatusCode.OK ? a : b;
        var loser = winner == a ? b : a;
        var winnerBody = await winner.Content.ReadFromJsonAsync<JsonElement>();
        var loserBody = await loser.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("concurrently", loserBody.GetProperty("message").GetString());

        // Stored state is exactly the winner's transition.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Applications.AsNoTracking().SingleAsync(x => x.Id == appId);
        Assert.Equal(winnerBody.GetProperty("status").GetString(), stored.Status.ToString().ToLowerInvariant());
        // Seed sets recruiter notes (version 1); the winner's transition is the only other write (version 2).
        Assert.Equal(2, stored.RowVersion);
    }

    [Fact]
    public async Task ApplyToClosedJob_Returns409AndStoresNothing()
    {
        var jobId = Guid.NewGuid();
        var factory = NewFactory(_ => TestJobCompany.OwnedJobResponse("Closed"));
        var applicant = As(factory, Guid.NewGuid(), "User");

        var response = await applicant.PostAsync("/api/applications", ApplyForm(jobId));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not accepting applications", body.GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Applications.AnyAsync(x => x.JobId == jobId));
    }

    [Fact]
    public async Task ApplyToActiveJob_Returns201WithContractFields()
    {
        var jobId = Guid.NewGuid();
        var factory = NewFactory();
        var applicant = As(factory, Guid.NewGuid(), "User");

        var response = await applicant.PostAsync("/api/applications", ApplyForm(jobId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var keys = body.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.True(keys.SetEquals(new[] { "id", "job_id", "applicant_id", "status", "cv_url", "message" }));
        Assert.Equal("pending", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ApplicantView_OverHttp_HasNoRecruiterNotes_RecruiterViewDoes()
    {
        var jobId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();
        var factory = NewFactory();
        var appId = await SeedApplicationAsync(factory, jobId, applicantId);
        var url = $"/api/applications/{appId}";

        var asApplicant = await As(factory, applicantId, "User").GetFromJsonAsync<JsonElement>(url);
        var asRecruiter = await As(factory, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true).GetFromJsonAsync<JsonElement>(url);

        Assert.False(asApplicant.TryGetProperty("recruiterNotes", out _));
        Assert.Equal("Internal note", asRecruiter.GetProperty("recruiterNotes").GetString());
    }

    [Fact]
    public async Task StatusUpdate_Response_HasContractFields()
    {
        var jobId = Guid.NewGuid();
        var factory = NewFactory();
        var appId = await SeedApplicationAsync(factory, jobId, Guid.NewGuid());
        var recruiter = As(factory, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);

        var response = await recruiter.PutAsJsonAsync($"/api/applications/{appId}/status", new { status = "reviewed" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(
            new[] { "id", "message", "previous_status", "status", "next_allowed_statuses" }));
        Assert.Equal("pending", body.GetProperty("previous_status").GetString());
        Assert.Equal("reviewed", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OverflowingPage_OnMyApplications_Returns400()
    {
        var factory = NewFactory();
        var applicant = As(factory, Guid.NewGuid(), "User");

        var response = await applicant.GetAsync("/api/applications/me?page=30000001&size=100");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OverflowingPage_OnJobApplications_Returns400()
    {
        var factory = NewFactory();
        var recruiter = As(factory, Guid.NewGuid(), "Recruiter", linkedToOwnedCompany: true);

        var response = await recruiter.GetAsync($"/api/applications/job/{Guid.NewGuid()}?page=30000001&size=100");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ValidPage_OnMyApplications_Returns200()
    {
        var factory = NewFactory();
        var applicant = As(factory, Guid.NewGuid(), "User");

        var response = await applicant.GetAsync("/api/applications/me?page=1&size=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<(HttpResponseMessage A, HttpResponseMessage B)> TaskPair(
        Task<HttpResponseMessage> a, Task<HttpResponseMessage> b)
    {
        await Task.WhenAll(a, b);
        return (a.Result, b.Result);
    }
}
