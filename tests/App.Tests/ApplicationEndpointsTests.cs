using System.Security.Claims;
using System.Text;
using App.Api.Endpoints;
using App.Core.DTOs;
using App.Core.Entities;
using App.Infrastructure.Data;
using App.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace App.Tests;

public class ApplicationEndpointsTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly LocalCvStorageService _storage;
    private readonly string _testStorageDir;
    private readonly Guid _applicantId = Guid.NewGuid();
    private readonly Guid _recruiterId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _jobId = Guid.NewGuid();

    public ApplicationEndpointsTests()
    {
        var dbName = "AppTestDb_" + Guid.NewGuid();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _testStorageDir = Path.Combine(Path.GetTempPath(), "app_test_storage_" + Guid.NewGuid());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APP_CV_STORAGE_PATH"] = _testStorageDir
            })
            .Build();
        _storage = new LocalCvStorageService(config, NullLogger<LocalCvStorageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_testStorageDir))
        {
            try { Directory.Delete(_testStorageDir, true); } catch { }
        }
    }

    private static HttpContext BuildContext(Guid? userId, string role = "User")
    {
        var ctx = new DefaultHttpContext();
        if (userId.HasValue)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.Value.ToString()),
                new(ClaimTypes.Role, role)
            };
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
            ctx.Request.Headers["X-User-Id"] = userId.Value.ToString();
            ctx.Request.Headers["X-User-Role"] = role;
        }
        return ctx;
    }

    private static HttpRequest BuildMultipartRequest(
        Dictionary<string, string> fields,
        string fileName = "resume.pdf",
        byte[]? fileBytes = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "multipart/form-data; boundary=----WebKitFormBoundary7MA4YWxkTrZu0gW";

        var formFields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        foreach (var kvp in fields)
        {
            formFields[kvp.Key] = kvp.Value;
        }

        var bytes = fileBytes ?? Encoding.UTF8.GetBytes("%PDF-1.4 Dummy PDF content");
        var fileStream = new MemoryStream(bytes);
        var formFile = new FormFile(fileStream, 0, bytes.Length, "cv_file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

        var files = new FormFileCollection { formFile };
        ctx.Request.Form = new FormCollection(formFields, files);
        return ctx.Request;
    }

    [Fact]
    public async Task ApplyForJob_ValidSubmission_Returns201CreatedAndPersistsApplication()
    {
        var ctx = BuildContext(_applicantId, "User");
        var fields = new Dictionary<string, string>
        {
            ["job_id"] = _jobId.ToString(),
            ["cover_letter"] = "I am a passionate software engineer."
        };
        var req = BuildMultipartRequest(fields, "cv.pdf");

        var result = await ApplicationEndpoints.ApplyForJob(req, _db, _storage, ctx);

        Assert.NotNull(result);
        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(201, statusCodeResult.StatusCode);

        // Verify database
        var app = await _db.Applications.Include(a => a.StatusHistories).FirstOrDefaultAsync(a => a.JobId == _jobId && a.ApplicantId == _applicantId);
        Assert.NotNull(app);
        Assert.Equal(ApplicationStatus.Pending, app.Status);
        Assert.Equal("I am a passionate software engineer.", app.CoverLetter);
        Assert.StartsWith("/api/applications/cv/", app.CvUrl);
        Assert.Single(app.StatusHistories);
        Assert.Equal(ApplicationStatus.Pending, app.StatusHistories.First().Status);
    }

    [Fact]
    public async Task ApplyForJob_DuplicateApplication_Returns409Conflict()
    {
        // Seed first application
        var existingApp = new Application(_jobId, _applicantId, "/api/applications/cv/initial.pdf");
        _db.Applications.Add(existingApp);
        await _db.SaveChangesAsync();

        var ctx = BuildContext(_applicantId, "User");
        var fields = new Dictionary<string, string>
        {
            ["job_id"] = _jobId.ToString(),
            ["cover_letter"] = "Applying again"
        };
        var req = BuildMultipartRequest(fields, "cv2.pdf");

        var result = await ApplicationEndpoints.ApplyForJob(req, _db, _storage, ctx);

        Assert.NotNull(result);
        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(409, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task ApplyForJob_Unauthenticated_Returns401()
    {
        var ctx = BuildContext(null); // No user ID
        var fields = new Dictionary<string, string> { ["job_id"] = _jobId.ToString() };
        var req = BuildMultipartRequest(fields);

        var result = await ApplicationEndpoints.ApplyForJob(req, _db, _storage, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(401, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetMyApplications_ReturnsCandidatesApplications()
    {
        var app1 = new Application(_jobId, _applicantId, "/cv1.pdf");
        var otherJob = Guid.NewGuid();
        var app2 = new Application(otherJob, _applicantId, "/cv2.pdf");
        var thirdPartyApp = new Application(_jobId, _otherUserId, "/cv3.pdf");

        _db.Applications.AddRange(app1, app2, thirdPartyApp);
        await _db.SaveChangesAsync();

        var ctx = BuildContext(_applicantId, "User");
        var result = await ApplicationEndpoints.GetMyApplications(null, 1, 10, _db, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(200, statusCodeResult.StatusCode);

        var valueResult = result as IValueHttpResult;
        var paginated = valueResult?.Value as PaginatedResponse<ApplicationSummaryDto>;
        Assert.NotNull(paginated);
        Assert.Equal(2, paginated.Total);
        Assert.Equal(2, paginated.Items.Count);
    }

    [Fact]
    public async Task GetApplicationById_OwnerOrRecruiter_CanAccess()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf", "Cover letter text");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        // Applicant can access
        var applicantCtx = BuildContext(_applicantId, "User");
        var result = await ApplicationEndpoints.GetApplicationById(app.Id, _db, applicantCtx);
        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(200, statusCodeResult.StatusCode);

        var valueResult = result as IValueHttpResult;
        var detail = valueResult?.Value as ApplicationDetailDto;
        Assert.NotNull(detail);
        Assert.Equal(app.Id, detail.Id);
        Assert.Equal("Cover letter text", detail.CoverLetter);

        // Recruiter can access
        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");
        var recruiterResult = await ApplicationEndpoints.GetApplicationById(app.Id, _db, recruiterCtx);
        Assert.Equal(200, (recruiterResult as IStatusCodeHttpResult)?.StatusCode);

        // Unauthorized user cannot access (403)
        var unauthorizedCtx = BuildContext(_otherUserId, "User");
        var forbiddenResult = await ApplicationEndpoints.GetApplicationById(app.Id, _db, unauthorizedCtx);
        Assert.Equal(403, (forbiddenResult as IStatusCodeHttpResult)?.StatusCode);
    }

    [Fact]
    public async Task UpdateApplicationStatus_Recruiter_UpdatesStatusAndAddsHistory()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");
        var updateReq = new UpdateStatusRequest("reviewed", "Passed initial CV screening");

        var result = await ApplicationEndpoints.UpdateApplicationStatus(app.Id, updateReq, _db, recruiterCtx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(200, statusCodeResult.StatusCode);

        // Verify DB
        var updated = await _db.Applications.Include(a => a.StatusHistories).FirstAsync(a => a.Id == app.Id);
        Assert.Equal(ApplicationStatus.Reviewed, updated.Status);
        Assert.Equal(2, updated.StatusHistories.Count);
        Assert.Equal(ApplicationStatus.Reviewed, updated.StatusHistories.Last().Status);
        Assert.Equal("Passed initial CV screening", updated.StatusHistories.Last().Note);
    }

    [Fact]
    public async Task UpdateApplicationStatus_FullPipeline_ProgressesThroughAllStages()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");

        // 1. Pending -> Reviewed
        var res1 = await ApplicationEndpoints.UpdateApplicationStatus(
            app.Id, new UpdateStatusRequest("reviewed", "CV looks good"), _db, recruiterCtx);
        Assert.Equal(200, (res1 as IStatusCodeHttpResult)?.StatusCode);

        // 2. Reviewed -> Shortlisted
        var res2 = await ApplicationEndpoints.UpdateApplicationStatus(
            app.Id, new UpdateStatusRequest("shortlisted", "Invite to technical round"), _db, recruiterCtx);
        Assert.Equal(200, (res2 as IStatusCodeHttpResult)?.StatusCode);

        // 3. Shortlisted -> Accepted (with score & notes)
        var res3 = await ApplicationEndpoints.UpdateApplicationStatus(
            app.Id,
            new UpdateStatusRequest("accepted", "Offer accepted by candidate", "Hired as Mid-level Dev", 92.5),
            _db,
            recruiterCtx);
        Assert.Equal(200, (res3 as IStatusCodeHttpResult)?.StatusCode);

        // Verify final state
        var finalApp = await _db.Applications.Include(a => a.StatusHistories).FirstAsync(a => a.Id == app.Id);
        Assert.Equal(ApplicationStatus.Accepted, finalApp.Status);
        Assert.Equal(92.5, finalApp.Score);
        Assert.Equal("Hired as Mid-level Dev", finalApp.RecruiterNotes);
        Assert.Equal(4, finalApp.StatusHistories.Count);
    }

    [Fact]
    public async Task UpdateApplicationStatus_InvalidTransition_Returns409Conflict()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");

        // Pending -> Accepted is illegal (must go through review/shortlist)
        var updateReq = new UpdateStatusRequest("accepted", "Cannot jump directly");
        var result = await ApplicationEndpoints.UpdateApplicationStatus(app.Id, updateReq, _db, recruiterCtx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(409, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task UpdateApplicationStatus_TerminalState_Returns409Conflict()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");

        // Reject application
        await ApplicationEndpoints.UpdateApplicationStatus(app.Id, new UpdateStatusRequest("rejected", "Not qualified"), _db, recruiterCtx);

        // Attempt to move from Rejected to Reviewed
        var retryResult = await ApplicationEndpoints.UpdateApplicationStatus(app.Id, new UpdateStatusRequest("reviewed"), _db, recruiterCtx);
        Assert.Equal(409, (retryResult as IStatusCodeHttpResult)?.StatusCode);
    }

    [Fact]
    public async Task GetApplicationHistory_OwnerCandidateAndRecruiter_CanRetrieveAuditTrail()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");
        await ApplicationEndpoints.UpdateApplicationStatus(
            app.Id, new UpdateStatusRequest("reviewed", "Review completed"), _db, recruiterCtx);
        await ApplicationEndpoints.UpdateApplicationStatus(
            app.Id, new UpdateStatusRequest("shortlisted", "Shortlisted for interview"), _db, recruiterCtx);

        // 1. Candidate views own application history
        var applicantCtx = BuildContext(_applicantId, "User");
        var candidateResult = await ApplicationEndpoints.GetApplicationHistory(app.Id, _db, applicantCtx);
        Assert.Equal(200, (candidateResult as IStatusCodeHttpResult)?.StatusCode);

        var history = (candidateResult as IValueHttpResult)?.Value as IReadOnlyList<StatusHistoryDto>;
        Assert.NotNull(history);
        Assert.Equal(3, history.Count);
        Assert.Equal("pending", history[0].Status);
        Assert.Equal("reviewed", history[1].Status);
        Assert.Equal("shortlisted", history[2].Status);

        // 2. Recruiter views history
        var recruiterResult = await ApplicationEndpoints.GetApplicationHistory(app.Id, _db, recruiterCtx);
        Assert.Equal(200, (recruiterResult as IStatusCodeHttpResult)?.StatusCode);

        // 3. Unauthorized other candidate receives 403 Forbidden
        var otherUserCtx = BuildContext(_otherUserId, "User");
        var forbiddenResult = await ApplicationEndpoints.GetApplicationHistory(app.Id, _db, otherUserCtx);
        Assert.Equal(403, (forbiddenResult as IStatusCodeHttpResult)?.StatusCode);
    }

    [Fact]
    public async Task GetAllowedTransitionsForApplication_ReturnsPermittedNextStatuses()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");
        var result = await ApplicationEndpoints.GetAllowedTransitionsForApplication(app.Id, _db, recruiterCtx);

        Assert.Equal(200, (result as IStatusCodeHttpResult)?.StatusCode);
    }

    [Fact]
    public void GetStatusFlowDefinition_ReturnsCompleteStateMachine()
    {
        var result = ApplicationEndpoints.GetStatusFlowDefinition();

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(200, statusCodeResult.StatusCode);

        var flow = (result as IValueHttpResult)?.Value as StatusFlowDto;
        Assert.NotNull(flow);
        Assert.Contains("pending", flow.AllStatuses);
        Assert.Contains("accepted", flow.TerminalStatuses);
        Assert.Contains("rejected", flow.TerminalStatuses);
        Assert.True(flow.Transitions.ContainsKey("pending"));
        Assert.True(flow.Transitions.ContainsKey("shortlisted"));
    }

    [Fact]
    public async Task GetMyApplications_WithInvalidStatus_ReturnsValidationProblem()
    {
        var ctx = BuildContext(_applicantId, "User");
        var result = await ApplicationEndpoints.GetMyApplications("non_existent_status", 1, 10, _db, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(400, statusCodeResult.StatusCode);
<<<<<<< HEAD
    }

    [Fact]
    public async Task DownloadCv_OwnerApplicant_ReturnsFile()
    {
        var pdfBytes = "%PDF-1.4 Content of CV"u8.ToArray();
        using var stream = new MemoryStream(pdfBytes);
        var cvUrl = await _storage.SaveCvAsync(stream, "my_cv.pdf", "application/pdf");
        var fileName = Path.GetFileName(cvUrl);

        var app = new Application(_jobId, _applicantId, cvUrl);
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var ctx = BuildContext(_applicantId, "User");
        var result = await ApplicationEndpoints.DownloadCv(fileName, _db, _storage, ctx);

        Assert.NotNull(result);
        Assert.IsAssignableFrom<IContentTypeHttpResult>(result);
    }

    [Fact]
    public async Task DownloadCv_Recruiter_ReturnsFile()
    {
        var pdfBytes = "%PDF-1.4 Content of CV"u8.ToArray();
        using var stream = new MemoryStream(pdfBytes);
        var cvUrl = await _storage.SaveCvAsync(stream, "my_cv.pdf", "application/pdf");
        var fileName = Path.GetFileName(cvUrl);

        var app = new Application(_jobId, _applicantId, cvUrl);
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var ctx = BuildContext(_recruiterId, "Recruiter");
        var result = await ApplicationEndpoints.DownloadCv(fileName, _db, _storage, ctx);

        Assert.NotNull(result);
        Assert.IsAssignableFrom<IContentTypeHttpResult>(result);
    }

    [Fact]
    public async Task DownloadCv_OtherUser_Returns403Forbidden()
    {
        var pdfBytes = "%PDF-1.4 Content of CV"u8.ToArray();
        using var stream = new MemoryStream(pdfBytes);
        var cvUrl = await _storage.SaveCvAsync(stream, "my_cv.pdf", "application/pdf");
        var fileName = Path.GetFileName(cvUrl);

        var app = new Application(_jobId, _applicantId, cvUrl);
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var ctx = BuildContext(_otherUserId, "User");
        var result = await ApplicationEndpoints.DownloadCv(fileName, _db, _storage, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(403, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task DownloadCv_Unauthenticated_Returns401Unauthorized()
    {
        var ctx = BuildContext(null);
        var result = await ApplicationEndpoints.DownloadCv("some_cv.pdf", _db, _storage, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(401, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task DownloadCv_NonExistentCv_Returns404NotFound()
    {
        var ctx = BuildContext(_applicantId, "User");
        var result = await ApplicationEndpoints.DownloadCv("nonexistent.pdf", _db, _storage, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(404, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task ApplyForJob_DbUpdateExceptionRace_Returns409AndDeletesOrphanCv()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("ThrowingDb_" + Guid.NewGuid())
            .Options;
        using var throwingDb = new ThrowingAppDbContext(dbOptions);

        var ctx = BuildContext(_applicantId, "User");
        var fields = new Dictionary<string, string>
        {
            ["job_id"] = _jobId.ToString(),
            ["cover_letter"] = "Race condition test"
        };
        var req = BuildMultipartRequest(fields, "race_cv.pdf");

        var result = await ApplicationEndpoints.ApplyForJob(req, throwingDb, _storage, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(409, statusCodeResult.StatusCode);

        // Verify storage directory has no orphan files left
        var remainingFiles = Directory.GetFiles(_testStorageDir);
        Assert.Empty(remainingFiles);
    }

    [Fact]
    public async Task ApplyForJob_InvalidFileSignature_Returns400BadRequest()
    {
        var ctx = BuildContext(_applicantId, "User");
        var fields = new Dictionary<string, string> { ["job_id"] = _jobId.ToString() };
        var badPdfBytes = "Not a PDF document"u8.ToArray();
        var req = BuildMultipartRequest(fields, "cv.pdf", badPdfBytes);

        var result = await ApplicationEndpoints.ApplyForJob(req, _db, _storage, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(400, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task ApplyForJob_MissingStrictCvFileField_Returns400ValidationProblem()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "multipart/form-data; boundary=----WebKitFormBoundary7MA4YWxkTrZu0gW";

        var formFields = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["job_id"] = _jobId.ToString()
        };
        var fileStream = new MemoryStream("%PDF-1.4 content"u8.ToArray());
        var formFile = new FormFile(fileStream, 0, fileStream.Length, "wrong_field_name", "cv.pdf");
        ctx.Request.Form = new FormCollection(formFields, new FormFileCollection { formFile });

        var userCtx = BuildContext(_applicantId, "User");
        var result = await ApplicationEndpoints.ApplyForJob(ctx.Request, _db, _storage, userCtx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(400, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task UpdateApplicationStatus_NoteExceeding1000Chars_Returns400ValidationProblem()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");
        var longNote = new string('A', 1001);
        var updateReq = new UpdateStatusRequest("reviewed", longNote);

        var result = await ApplicationEndpoints.UpdateApplicationStatus(app.Id, updateReq, _db, recruiterCtx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(400, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetMyApplications_InvalidStatus_Returns400ValidationProblem()
    {
        var ctx = BuildContext(_applicantId, "User");
        var result = await ApplicationEndpoints.GetMyApplications("invalid_status_xyz", 1, 10, _db, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(400, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetApplicationsByJobId_InvalidStatus_Returns400ValidationProblem()
    {
        var ctx = BuildContext(_recruiterId, "Recruiter");
        var result = await ApplicationEndpoints.GetApplicationsByJobId(_jobId, "unknown_status", 1, 10, _db, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(400, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task ApplyForJob_HeaderOnlyWithoutDevAuth_Returns401Unauthorized()
    {
        // Setup HttpContext with header but without ClaimsPrincipal (non-dev simulation)
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-User-Id"] = _applicantId.ToString();
        ctx.Request.Headers["X-User-Role"] = "User";

        var fields = new Dictionary<string, string> { ["job_id"] = _jobId.ToString() };
        var req = BuildMultipartRequest(fields);

        var result = await ApplicationEndpoints.ApplyForJob(req, _db, _storage, ctx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(401, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task DevAuthMiddleware_WhenFlagDisabled_DoesNotSetUser()
    {
        var env = new FakeEnv { EnvironmentName = "Development" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ENABLE_DEV_AUTH"] = "false"
        }).Build();

        var middleware = new App.Api.Middleware.DevAuthMiddleware(
            _ => Task.CompletedTask,
            NullLogger<App.Api.Middleware.DevAuthMiddleware>.Instance,
            config,
            env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-User-Id"] = Guid.NewGuid().ToString();

        await middleware.InvokeAsync(ctx);

        Assert.False(ctx.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task DevAuthMiddleware_WhenFlagEnabledInDev_SetsUser()
    {
        var env = new FakeEnv { EnvironmentName = "Development" };
        var testUserId = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ENABLE_DEV_AUTH"] = "true"
        }).Build();

        var middleware = new App.Api.Middleware.DevAuthMiddleware(
            _ => Task.CompletedTask,
            NullLogger<App.Api.Middleware.DevAuthMiddleware>.Instance,
            config,
            env);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-User-Id"] = testUserId.ToString();
        ctx.Request.Headers["X-User-Role"] = "Recruiter";

        await middleware.InvokeAsync(ctx);

        Assert.True(ctx.User.Identity?.IsAuthenticated);
        Assert.Equal(testUserId.ToString(), ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Recruiter", ctx.User.FindFirst(ClaimTypes.Role)?.Value);
    }

    [Fact]
    public async Task UpdateApplicationStatus_PendingToShortlistedDirectly_Returns409Conflict()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");
        var updateReq = new UpdateStatusRequest("shortlisted", "Skipping reviewed directly");

        var result = await ApplicationEndpoints.UpdateApplicationStatus(app.Id, updateReq, _db, recruiterCtx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(409, statusCodeResult.StatusCode);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    public async Task UpdateApplicationStatus_ScoreOutOfRange_Returns400ValidationProblem(double invalidScore)
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");
        var updateReq = new UpdateStatusRequest("reviewed", "Valid note", Score: invalidScore);

        var result = await ApplicationEndpoints.UpdateApplicationStatus(app.Id, updateReq, _db, recruiterCtx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(400, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task UpdateApplicationStatus_RecruiterNotesTooLong_Returns400ValidationProblem()
    {
        var app = new Application(_jobId, _applicantId, "/cv.pdf");
        _db.Applications.Add(app);
        await _db.SaveChangesAsync();

        var recruiterCtx = BuildContext(_recruiterId, "Recruiter");
        var longNotes = new string('N', Application.RecruiterNotesMaxLength + 1);
        var updateReq = new UpdateStatusRequest("reviewed", "Valid note", RecruiterNotes: longNotes);

        var result = await ApplicationEndpoints.UpdateApplicationStatus(app.Id, updateReq, _db, recruiterCtx);

        var statusCodeResult = result as IStatusCodeHttpResult;
        Assert.NotNull(statusCodeResult);
        Assert.Equal(400, statusCodeResult.StatusCode);
    }

    [Fact]
    public void GetStatusFlowDefinition_SerializesToSnakeCase()
    {
        var result = ApplicationEndpoints.GetStatusFlowDefinition();
        var flow = (result as IValueHttpResult)?.Value as StatusFlowDto;
        Assert.NotNull(flow);

        var json = System.Text.Json.JsonSerializer.Serialize(flow);
        Assert.Contains("\"all_statuses\":", json);
        Assert.Contains("\"terminal_statuses\":", json);
        Assert.Contains("\"transitions\":", json);
    }

    [Fact]
    public async Task ApplyForJob_NonUniqueDbUpdateException_CleansUpOrphanCvAndRethrows()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("GenericThrowingDb_" + Guid.NewGuid())
            .Options;
        using var throwingDb = new NonUniqueThrowingAppDbContext(dbOptions);

        var ctx = BuildContext(_applicantId, "User");
        var fields = new Dictionary<string, string>
        {
            ["job_id"] = _jobId.ToString(),
            ["cover_letter"] = "Non unique error test"
        };
        var req = BuildMultipartRequest(fields, "non_unique_cv.pdf");

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            ApplicationEndpoints.ApplyForJob(req, throwingDb, _storage, ctx));

        // Verify orphan CV file was cleaned up on disk even though exception rethrown
        var remainingFiles = Directory.GetFiles(_testStorageDir);
        Assert.Empty(remainingFiles);
    }

    private class NonUniqueThrowingAppDbContext : AppDbContext
    {
        public NonUniqueThrowingAppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("Connection timeout failure", new Exception("Timeout occurred"));
        }
    }

    private class FakeEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "App.Api";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private class ThrowingAppDbContext : AppDbContext
    {
        public ThrowingAppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            throw new DbUpdateException("Duplicate key violation on IX_applications_job_applicant_unique", new Exception());
        }
=======
>>>>>>> 51cf00ad85077f8148705bed9cb84ef939879c08
    }
}
