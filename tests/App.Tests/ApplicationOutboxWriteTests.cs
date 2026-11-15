using System.Security.Claims;
using System.Text;
using System.Text.Json;
using App.Api.Endpoints;
using App.Api.Services;
using App.Core.DTOs;
using App.Core.Entities;
using App.Infrastructure.Data;
using App.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using SharedKernel.Events;
using SharedKernel.Kafka;
using Xunit;

namespace App.Tests;

/// <summary>
/// Application writes must add their outbox row in the same SaveChanges as the application change, and a
/// request that does not write must add none. The in-memory provider has no transactions, so a rolled-back
/// commit is only simulated here (the save throws before anything is stored). Real rollback is covered by
/// PostgreSQL's single-statement transaction and needs the infra stack.
/// </summary>
public class ApplicationOutboxWriteTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly LocalCvStorageService _storage;
    private readonly string _storageDir;
    private readonly Guid _applicantId = Guid.NewGuid();
    private readonly Guid _recruiterId = Guid.NewGuid();
    private readonly Guid _jobId = Guid.NewGuid();
    private readonly JobServiceClient _jobClient = TestJobCompany.Owned();

    public ApplicationOutboxWriteTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("OutboxWrite_" + Guid.NewGuid())
            .Options);
        _storageDir = Path.Combine(Path.GetTempPath(), "app_outbox_write_" + Guid.NewGuid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_CV_STORAGE_PATH"] = _storageDir })
            .Build();
        _storage = new LocalCvStorageService(config, NullLogger<LocalCvStorageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, true); } catch { }
        }
    }

    private static HttpContext UserContext(Guid userId, string role)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, role),
        };
        if (role == "Recruiter")
            claims.Add(new Claim("company_id", TestJobCompany.CompanyId.ToString()));

        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        return ctx;
    }

    private static HttpRequest ApplyRequest(Guid jobId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "multipart/form-data; boundary=----OutboxBoundary";
        var fields = new Dictionary<string, StringValues> { ["job_id"] = jobId.ToString() };
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 outbox test");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "cv_file", "cv.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf",
        };
        ctx.Request.Form = new FormCollection(fields, new FormFileCollection { file });
        return ctx.Request;
    }

    private async Task<Guid> SeedApplicationAsync(ApplicationStatus status = ApplicationStatus.Pending)
    {
        var app = new Application(_jobId, _applicantId, "/api/applications/cv/seed.pdf");
        if (status != ApplicationStatus.Pending)
            app.UpdateStatus(status, _recruiterId, null);
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return app.Id;
    }

    private async Task<List<OutboxEvent>> OutboxRowsAsync() =>
        await _db.OutboxEvents.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync();

    private sealed class CountingAppDbContext : AppDbContext
    {
        public int SaveCalls { get; private set; }

        public CountingAppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCalls++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class FailingSaveAppDbContext : AppDbContext
    {
        public FailingSaveAppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("commit failed", new Exception("connection lost"));
    }

    [Fact]
    public async Task ApplyForJob_WritesApplicationAndSubmittedRowInOneSaveChanges()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("OutboxApply_" + Guid.NewGuid())
            .Options;
        using var db = new CountingAppDbContext(options);

        var result = await ApplicationEndpoints.ApplyForJob(
            ApplyRequest(_jobId), db, _storage, UserContext(_applicantId, "User"), jobClient: _jobClient);

        Assert.Equal(201, (result as IStatusCodeHttpResult)?.StatusCode);
        Assert.Equal(1, db.SaveCalls);
        var application = await db.Applications.SingleAsync();
        var row = await db.OutboxEvents.SingleAsync();
        Assert.Equal(ApplicationEventTypes.Submitted, row.EventType);
        Assert.Equal(application.Id, row.AggregateId);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<ApplicationSubmittedEvent>>(row.Payload, KafkaJson.Options)!;
        Assert.Equal(TestJobCompany.Title, envelope.Payload.JobTitle);
        Assert.Equal(_applicantId, envelope.Payload.ApplicantId);
    }

    [Fact]
    public async Task ApplyForJob_DuplicateApplication_WritesNoOutboxRow()
    {
        await SeedApplicationAsync();

        var result = await ApplicationEndpoints.ApplyForJob(
            ApplyRequest(_jobId), _db, _storage, UserContext(_applicantId, "User"), jobClient: _jobClient);

        Assert.Equal(409, (result as IStatusCodeHttpResult)?.StatusCode);
        Assert.Empty(await OutboxRowsAsync());
    }

    [Fact]
    public async Task ApplyForJob_FailedCommit_LeavesNoApplicationAndNoOutboxRow()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("OutboxFailedCommit_" + Guid.NewGuid())
            .Options;
        using var db = new FailingSaveAppDbContext(options);

        await Assert.ThrowsAsync<DbUpdateException>(() => ApplicationEndpoints.ApplyForJob(
            ApplyRequest(_jobId), db, _storage, UserContext(_applicantId, "User"), jobClient: _jobClient));

        Assert.Empty(await db.OutboxEvents.ToListAsync());
        Assert.Empty(await db.Applications.ToListAsync());
    }

    [Fact]
    public async Task UpdateStatus_WritesOneStatusChangedRowWithPreviousAndNewStatus()
    {
        var appId = await SeedApplicationAsync();

        var result = await ApplicationEndpoints.UpdateApplicationStatus(
            appId, new UpdateStatusRequest("reviewed", null), _db, UserContext(_recruiterId, "Recruiter"), jobClient: _jobClient);

        Assert.Equal(200, (result as IStatusCodeHttpResult)?.StatusCode);
        var row = Assert.Single(await OutboxRowsAsync());
        Assert.Equal(ApplicationEventTypes.StatusChanged, row.EventType);
        Assert.Equal(appId, row.AggregateId);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<ApplicationStatusChangedEvent>>(row.Payload, KafkaJson.Options)!;
        Assert.Equal("Pending", envelope.Payload.PreviousStatus);
        Assert.Equal("Reviewed", envelope.Payload.NewStatus);
        Assert.Equal(_recruiterId, envelope.Payload.ChangedBy);
        Assert.Equal(TestJobCompany.Title, envelope.Payload.JobTitle);
    }

    [Fact]
    public async Task UpdateStatus_RejectedTransition_WritesNoOutboxRow()
    {
        var appId = await SeedApplicationAsync(ApplicationStatus.Rejected);

        var result = await ApplicationEndpoints.UpdateApplicationStatus(
            appId, new UpdateStatusRequest("reviewed", null), _db, UserContext(_recruiterId, "Recruiter"), jobClient: _jobClient);

        Assert.Equal(409, (result as IStatusCodeHttpResult)?.StatusCode);
        Assert.Empty(await OutboxRowsAsync());
    }
}
