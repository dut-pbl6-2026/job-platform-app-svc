using App.Api.Endpoints;
using App.Api.Services;
using App.Infrastructure.Services;
using App.Core.DTOs;
using App.Core.Entities;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace App.Tests;

/// <summary>
/// Regression tests for recruiter access to applications. A recruiter may only see or act on
/// applications for jobs owned by their company (JWT company_id vs job-svc CompanyId). Access
/// fails closed when ownership cannot be verified.
/// </summary>
public class ApplicationOwnershipTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly LocalCvStorageService _storage;
    private readonly string _storageDir;
    private readonly Guid _jobId = Guid.NewGuid();
    private readonly Guid _applicantId = Guid.NewGuid();
    private readonly Guid _ownRecruiter = Guid.NewGuid();
    private readonly Guid _otherRecruiter = Guid.NewGuid();
    private readonly Application _app;

    public ApplicationOwnershipTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("OwnershipDb_" + Guid.NewGuid())
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _storageDir = Path.Combine(Path.GetTempPath(), "ownership_storage_" + Guid.NewGuid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["APP_CV_STORAGE_PATH"] = _storageDir })
            .Build();
        _storage = new LocalCvStorageService(config, NullLogger<LocalCvStorageService>.Instance);

        _app = new Application(_jobId, _applicantId, "/api/applications/cv/secret-cv.pdf");
        _db.Applications.Add(_app);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, true); } catch { }
        }
    }

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 200;

    // ── Recruiter from another company is denied on every read path ──

    [Fact]
    public async Task OtherCompanyRecruiter_CannotListApplicantsForJob()
    {
        var ctx = TestJobCompany.RecruiterContext(_otherRecruiter, TestJobCompany.OtherCompanyId);
        var result = await ApplicationEndpoints.GetApplicationsByJobId(_jobId, null, 1, 20, _db, ctx,
            TestJobCompany.ForCompany(TestJobCompany.CompanyId));
        Assert.Equal(403, Status(result));
    }

    [Fact]
    public async Task OtherCompanyRecruiter_CannotReadApplicationDetail()
    {
        var ctx = TestJobCompany.RecruiterContext(_otherRecruiter, TestJobCompany.OtherCompanyId);
        var result = await ApplicationEndpoints.GetApplicationById(_app.Id, _db, ctx,
            TestJobCompany.ForCompany(TestJobCompany.CompanyId));
        Assert.Equal(403, Status(result));
    }

    [Fact]
    public async Task OtherCompanyRecruiter_CannotReadHistory()
    {
        var ctx = TestJobCompany.RecruiterContext(_otherRecruiter, TestJobCompany.OtherCompanyId);
        var result = await ApplicationEndpoints.GetApplicationHistory(_app.Id, _db, ctx,
            TestJobCompany.ForCompany(TestJobCompany.CompanyId));
        Assert.Equal(403, Status(result));
    }

    [Fact]
    public async Task OtherCompanyRecruiter_CannotReadAllowedTransitions()
    {
        var ctx = TestJobCompany.RecruiterContext(_otherRecruiter, TestJobCompany.OtherCompanyId);
        var result = await ApplicationEndpoints.GetAllowedTransitionsForApplication(_app.Id, _db, ctx,
            TestJobCompany.ForCompany(TestJobCompany.CompanyId));
        Assert.Equal(403, Status(result));
    }

    [Fact]
    public async Task OtherCompanyRecruiter_CannotDownloadCv()
    {
        var ctx = TestJobCompany.RecruiterContext(_otherRecruiter, TestJobCompany.OtherCompanyId);
        var result = await ApplicationEndpoints.DownloadCv("secret-cv.pdf", _db, _storage, ctx,
            TestJobCompany.ForCompany(TestJobCompany.CompanyId));
        Assert.Equal(403, Status(result));
    }

    [Fact]
    public async Task OtherCompanyRecruiter_CannotChangeStatus()
    {
        var ctx = TestJobCompany.RecruiterContext(_otherRecruiter, TestJobCompany.OtherCompanyId);
        var result = await ApplicationEndpoints.UpdateApplicationStatus(_app.Id,
            new UpdateStatusRequest("reviewed", "tampered"), _db, ctx,
            jobClient: TestJobCompany.ForCompany(TestJobCompany.CompanyId));
        Assert.Equal(403, Status(result));

        var stored = _db.Applications.Single(a => a.Id == _app.Id);
        Assert.Equal(ApplicationStatus.Pending, stored.Status);
    }

    // ── Fail closed ──

    [Fact]
    public async Task RecruiterWithoutCompanyClaim_IsDenied()
    {
        var ctx = TestJobCompany.RecruiterContext(_ownRecruiter, companyId: null);
        var result = await ApplicationEndpoints.GetApplicationsByJobId(_jobId, null, 1, 20, _db, ctx,
            TestJobCompany.Owned());
        Assert.Equal(403, Status(result));
    }

    [Fact]
    public async Task JobServiceDown_DeniesRecruiter()
    {
        var ctx = TestJobCompany.RecruiterContext(_ownRecruiter, TestJobCompany.CompanyId);
        var result = await ApplicationEndpoints.GetApplicationsByJobId(_jobId, null, 1, 20, _db, ctx,
            TestJobCompany.Down());
        Assert.Equal(503, Status(result));
    }

    [Fact]
    public async Task NoJobServiceClient_DeniesRecruiter()
    {
        var ctx = TestJobCompany.RecruiterContext(_ownRecruiter, TestJobCompany.CompanyId);
        var result = await ApplicationEndpoints.GetApplicationsByJobId(_jobId, null, 1, 20, _db, ctx, null);
        Assert.Equal(503, Status(result));
    }

    [Fact]
    public async Task UnknownJob_ReturnsNotFoundForRecruiter()
    {
        var ctx = TestJobCompany.RecruiterContext(_ownRecruiter, TestJobCompany.CompanyId);
        var result = await ApplicationEndpoints.GetApplicationsByJobId(_jobId, null, 1, 20, _db, ctx,
            TestJobCompany.Missing());
        Assert.Equal(404, Status(result));
    }

    // ── Allowed paths still work ──

    [Fact]
    public async Task OwningRecruiter_CanListApplicantsForJob()
    {
        var ctx = TestJobCompany.RecruiterContext(_ownRecruiter, TestJobCompany.CompanyId);
        var result = await ApplicationEndpoints.GetApplicationsByJobId(_jobId, null, 1, 20, _db, ctx,
            TestJobCompany.Owned());
        Assert.Equal(200, Status(result));
    }

    [Fact]
    public async Task Admin_IsNotRestrictedByCompany()
    {
        var ctx = TestJobCompany.RecruiterContext(_ownRecruiter, companyId: null);
        ctx.User.Identities.First().RemoveClaim(ctx.User.Identities.First().FindFirst(System.Security.Claims.ClaimTypes.Role));
        ctx.User.Identities.First().AddClaim(new System.Security.Claims.Claim(
            System.Security.Claims.ClaimTypes.Role, "Admin"));
        var result = await ApplicationEndpoints.GetApplicationsByJobId(_jobId, null, 1, 20, _db, ctx,
            TestJobCompany.Down());
        Assert.Equal(200, Status(result));
    }

    [Fact]
    public async Task Applicant_CanReadOwnApplication_EvenWhenJobServiceIsDown()
    {
        var ctx = BuildApplicantContext(_applicantId);
        var result = await ApplicationEndpoints.GetApplicationById(_app.Id, _db, ctx, TestJobCompany.Down());
        Assert.Equal(200, Status(result));
    }

    private static HttpContext BuildApplicantContext(Guid userId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId.ToString()),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "User"),
            ],
            "TestAuth"));
        ctx.Request.Headers["X-User-Id"] = userId.ToString();
        ctx.Request.Headers["X-User-Role"] = "User";
        return ctx;
    }
}
