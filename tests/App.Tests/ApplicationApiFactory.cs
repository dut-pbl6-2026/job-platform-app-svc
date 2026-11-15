using App.Api.Services;
using App.Core.Interfaces;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace App.Tests;

/// <summary>
/// Hosts the real application pipeline in-process. The Npgsql DbContext is replaced by a per-factory
/// in-memory database, job-svc is replaced by a fake responder, and dev header auth is enabled so tests
/// can act as a user, recruiter or admin through HTTP headers.
/// </summary>
public sealed class ApplicationApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "ApiIntegration_" + Guid.NewGuid();
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "app_api_it_" + Guid.NewGuid());
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _jobResponder;

    /// <summary>Fake ai-svc. Scoring runs only when a test calls the worker, never in the background.</summary>
    public FakeResumeScoringClient ScoringClient { get; } = new();

    public ApplicationApiFactory(Func<HttpRequestMessage, HttpResponseMessage>? jobResponder = null)
    {
        _jobResponder = jobResponder ?? (_ => TestJobCompany.OwnedJobResponse());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ENABLE_DEV_AUTH", "true");
        builder.UseSetting("APP_CV_STORAGE_PATH", _storageDir);

        builder.ConfigureTestServices(services =>
        {
            // Drop the Npgsql registration (options and provider configuration) before adding InMemory,
            // otherwise EF sees two database providers in one service provider.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_databaseName));

            // The outbox publisher would send and mark rows in the background and race the assertions.
            // Tests run publish cycles explicitly (see OutboxPublisherTests).
            services.RemoveAll<IHostedService>();

            services.RemoveAll<JobServiceClient>();
            services.AddSingleton(TestJobCompany.WithResponder(_jobResponder));

            services.RemoveAll<IResumeScoringClient>();
            services.AddSingleton<IResumeScoringClient>(ScoringClient);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, true); } catch { }
        }
    }
}
