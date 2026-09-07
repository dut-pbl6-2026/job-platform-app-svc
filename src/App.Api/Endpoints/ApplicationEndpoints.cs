using System.Security.Claims;
using App.Core.DTOs;
using App.Core.Entities;
using App.Core.Interfaces;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Api.Endpoints;

public static class ApplicationEndpoints
{
    public static WebApplication MapApplicationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/applications").WithTags("Applications");

        // APP-01-01: Apply for a job with CV upload
        group.MapPost("/", ApplyForJob)
            .DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(10 * 1024 * 1024));

        // APP-01-03: View candidate application history
        group.MapGet("/me", GetMyApplications);

        // System-wide status flow state machine metadata
        group.MapGet("/status-flow", GetStatusFlowDefinition);

        // APP-01-04: View application details
        group.MapGet("/{id:guid}", GetApplicationById);

        // Application status transition audit history
        group.MapGet("/{id:guid}/history", GetApplicationHistory);

        // Allowed next status transitions for this application
        group.MapGet("/{id:guid}/allowed-transitions", GetAllowedTransitionsForApplication);

        // APP-01-05: View applications for a specific job (Recruiter)
        group.MapGet("/job/{jobId:guid}", GetApplicationsByJobId);

        // APP-01-06: Update application status (pending -> reviewed -> ...)
        group.MapPut("/{id:guid}/status", UpdateApplicationStatus);

        // CV file streaming / download
        group.MapGet("/cv/{fileName}", DownloadCv);

        return app;
    }

    private static (Guid? UserId, string Role) GetIdentity(HttpContext ctx)
    {
        var idStr = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                    ?? ctx.User.FindFirst("sub")?.Value;

        var role = ctx.User.FindFirst(ClaimTypes.Role)?.Value
                   ?? ctx.User.FindFirst("role")?.Value;

        if (idStr is null)
        {
            var env = ctx.RequestServices?.GetService<IWebHostEnvironment>();
            var config = ctx.RequestServices?.GetService<IConfiguration>();
            var isDevAuthEnabled = (env?.IsDevelopment() ?? false) &&
                                   ((config?.GetValue<bool>("ENABLE_DEV_AUTH") ?? false) ||
                                    string.Equals(Environment.GetEnvironmentVariable("ENABLE_DEV_AUTH"), "true", StringComparison.OrdinalIgnoreCase));

            if (isDevAuthEnabled)
            {
                idStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
                role ??= ctx.Request.Headers["X-User-Role"].FirstOrDefault()
                         ?? ctx.Request.Headers["X-Role"].FirstOrDefault();
            }
        }

        role ??= "User";
        return Guid.TryParse(idStr, out var id) ? (id, role) : (null, role);
    }

    private static IResult UnauthorizedResult(string message = "Unauthorized. Missing or invalid user identity.") =>
        Results.Json(new { message }, statusCode: 401);

    private static IResult ForbiddenResult(string message = "Forbidden. You do not have permission to perform this action.") =>
        Results.Json(new { message }, statusCode: 403);

    /// <summary>
    /// POST /api/applications
    /// Submits a job application with multipart form data (job_id, cover_letter, cv_file).
    /// Prevents duplicate applications per user per job (returns 409 Conflict).
    /// </summary>
    public static async Task<IResult> ApplyForJob(
        HttpRequest request,
        AppDbContext db,
        IFileStorageService storage,
        HttpContext ctx)
    {
        var (userId, _) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();

        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Content-Type must be multipart/form-data." });
        }

        var form = await request.ReadFormAsync();

        var jobIdStr = form["job_id"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(jobIdStr) || !Guid.TryParse(jobIdStr, out var jobId) || jobId == Guid.Empty)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["job_id"] = ["Valid job_id UUID is required."]
            });
        }

        var coverLetter = form["cover_letter"].FirstOrDefault();
        if (coverLetter != null && coverLetter.Length > Application.CoverLetterMaxLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cover_letter"] = [$"Cover letter exceeds maximum length of {Application.CoverLetterMaxLength} characters."]
            });
        }

        var cvFile = form.Files.GetFile("cv_file");
        if (cvFile is null || cvFile.Length == 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["cv_file"] = ["CV file with form field name 'cv_file' is required for application submission."]
            });
        }

        // APP-01-02: Prevent duplicate applications
        var alreadyApplied = await db.Applications.AnyAsync(a => a.JobId == jobId && a.ApplicantId == userId.Value);
        if (alreadyApplied)
        {
            return Results.Conflict(new
            {
                status = 409,
                message = "Bạn đã ứng tuyển vào công việc này trước đó (409 Conflict)."
            });
        }

        string cvUrl;
        try
        {
            using var stream = cvFile.OpenReadStream();
            cvUrl = await storage.SaveCvAsync(stream, cvFile.FileName, cvFile.ContentType);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }

        var application = new Application(jobId, userId.Value, cvUrl, coverLetter);
        db.Applications.Add(application);

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            var fileName = Path.GetFileName(cvUrl);
            await storage.DeleteCvAsync(fileName);

            return Results.Conflict(new
            {
                status = 409,
                message = "Bạn đã ứng tuyển vào công việc này trước đó (409 Conflict)."
            });
        }
        catch (Exception)
        {
            var fileName = Path.GetFileName(cvUrl);
            await storage.DeleteCvAsync(fileName);
            throw;
        }

        return Results.Created($"/api/applications/{application.Id}", new
        {
            id = application.Id,
            job_id = application.JobId,
            applicant_id = application.ApplicantId,
            status = application.Status.ToString().ToLowerInvariant(),
            cv_url = application.CvUrl,
            message = "Ứng tuyển thành công!"
        });
    }

    /// <summary>
    /// GET /api/applications/me
    /// Retrieves application history for authenticated candidate.
    /// </summary>
    public static async Task<IResult> GetMyApplications(
        string? status,
        int? page,
        int? size,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, _) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();

        var pageIndex = Math.Max(0, (page ?? 0) > 0 ? (page!.Value - 1) : 0);
        var pageSize = Math.Clamp(size ?? 20, 1, 100);

        var query = db.Applications.AsNoTracking().Where(a => a.ApplicantId == userId.Value);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<ApplicationStatus>(status, true, out var parsedStatus))
            {
                query = query.Where(a => a.Status == parsedStatus);
            }
            else
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Invalid status '{status}'. Allowed values: pending, reviewed, shortlisted, accepted, rejected."]
                });
            }
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(a => new ApplicationSummaryDto(
                a.Id,
                a.JobId,
                a.ApplicantId,
                a.Status.ToString().ToLowerInvariant(),
                a.CvUrl,
                a.CoverLetter,
                a.CreatedAt,
                a.UpdatedAt))
            .ToListAsync();

        return Results.Ok(new PaginatedResponse<ApplicationSummaryDto>(items, total, pageIndex + 1, pageSize));
    }

    /// <summary>
    /// GET /api/applications/{id}
    /// Returns application details including complete status history and permitted next transitions.
    /// </summary>
    public static async Task<IResult> GetApplicationById(
        Guid id,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();

        var app = await db.Applications
            .Include(a => a.StatusHistories)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id);

        if (app is null)
            return Results.NotFound(new { message = "Application not found." });

        // Access check: only applicant or recruiter/admin can view details
        var isOwner = app.ApplicantId == userId.Value;
        var isRecruiterOrAdmin = role.Equals("Recruiter", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

        if (!isOwner && !isRecruiterOrAdmin)
            return ForbiddenResult("You are not authorized to view this application.");

        var historyDtos = app.StatusHistories
            .OrderBy(h => h.ChangedAt)
            .Select(h => new StatusHistoryDto(
                h.Id,
                h.Status.ToString().ToLowerInvariant(),
                h.Note,
                h.ChangedBy,
                h.ChangedAt))
            .ToList();

        var nextAllowed = app.GetAllowedTransitions()
            .Select(s => s.ToString().ToLowerInvariant())
            .ToList();

        var detail = new ApplicationDetailDto(
            app.Id,
            app.JobId,
            app.ApplicantId,
            app.CoverLetter,
            app.CvUrl,
            app.Status.ToString().ToLowerInvariant(),
            app.RecruiterNotes,
            app.Score,
            app.CreatedAt,
            app.UpdatedAt,
            historyDtos,
            nextAllowed);

        return Results.Ok(detail);
    }

    /// <summary>
    /// GET /api/applications/{id}/history
    /// Retrieves chronological status transition history audit trail for an application.
    /// Accessible by the applicant who submitted it or recruiters/admins.
    /// </summary>
    public static async Task<IResult> GetApplicationHistory(
        Guid id,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();

        var app = await db.Applications
            .Include(a => a.StatusHistories)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id);

        if (app is null)
            return Results.NotFound(new { message = "Application not found." });

        var isOwner = app.ApplicantId == userId.Value;
        var isRecruiterOrAdmin = role.Equals("Recruiter", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

        if (!isOwner && !isRecruiterOrAdmin)
            return ForbiddenResult("You are not authorized to view the history for this application.");

        var history = app.StatusHistories
            .OrderBy(h => h.ChangedAt)
            .Select(h => new StatusHistoryDto(
                h.Id,
                h.Status.ToString().ToLowerInvariant(),
                h.Note,
                h.ChangedBy,
                h.ChangedAt))
            .ToList();

        return Results.Ok(history);
    }

    /// <summary>
    /// GET /api/applications/{id}/allowed-transitions
    /// Returns the next permitted status transitions for a specific application.
    /// Useful for frontends (React / Flutter) to dynamically render recruiter action buttons.
    /// </summary>
    public static async Task<IResult> GetAllowedTransitionsForApplication(
        Guid id,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();

        var app = await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        if (app is null)
            return Results.NotFound(new { message = "Application not found." });

        var isOwner = app.ApplicantId == userId.Value;
        var isRecruiterOrAdmin = role.Equals("Recruiter", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

        if (!isOwner && !isRecruiterOrAdmin)
            return ForbiddenResult("You are not authorized to view transition rules for this application.");

        return Results.Ok(new
        {
            id = app.Id,
            current_status = app.Status.ToString().ToLowerInvariant(),
            allowed_transitions = app.GetAllowedTransitions().Select(s => s.ToString().ToLowerInvariant()).ToList()
        });
    }

    /// <summary>
    /// GET /api/applications/status-flow
    /// Returns the entire system state transition diagram and available statuses.
    /// </summary>
    public static IResult GetStatusFlowDefinition()
    {
        var allStatuses = Enum.GetNames<ApplicationStatus>()
            .Select(s => s.ToLowerInvariant())
            .ToList();

        var terminalStatuses = new[]
        {
            ApplicationStatus.Accepted.ToString().ToLowerInvariant(),
            ApplicationStatus.Rejected.ToString().ToLowerInvariant()
        };

        var transitions = Application.AllowedTransitions
            .ToDictionary(
                kvp => kvp.Key.ToString().ToLowerInvariant(),
                kvp => (IReadOnlyList<string>)kvp.Value.Select(s => s.ToString().ToLowerInvariant()).ToList()
            );

        return Results.Ok(new StatusFlowDto(allStatuses, terminalStatuses, transitions));
    }

    /// <summary>
    /// GET /api/applications/job/{jobId}
    /// Returns all applications for a specific job posting (Recruiter view).
    /// </summary>
    public static async Task<IResult> GetApplicationsByJobId(
        Guid jobId,
        string? status,
        int? page,
        int? size,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();

        var isRecruiterOrAdmin = role.Equals("Recruiter", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

        if (!isRecruiterOrAdmin)
            return ForbiddenResult("Only recruiters or administrators can view job applications.");

        var pageIndex = Math.Max(0, (page ?? 0) > 0 ? (page!.Value - 1) : 0);
        var pageSize = Math.Clamp(size ?? 20, 1, 100);

        var query = db.Applications.AsNoTracking().Where(a => a.JobId == jobId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<ApplicationStatus>(status, true, out var parsedStatus))
            {
                query = query.Where(a => a.Status == parsedStatus);
            }
            else
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Invalid status '{status}'. Allowed values: pending, reviewed, shortlisted, accepted, rejected."]
                });
            }
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(a => new ApplicationSummaryDto(
                a.Id,
                a.JobId,
                a.ApplicantId,
                a.Status.ToString().ToLowerInvariant(),
                a.CvUrl,
                a.CoverLetter,
                a.CreatedAt,
                a.UpdatedAt))
            .ToListAsync();

        return Results.Ok(new PaginatedResponse<ApplicationSummaryDto>(items, total, pageIndex + 1, pageSize));
    }

    /// <summary>
    /// PUT /api/applications/{id}/status
    /// Updates status of an application adhering to the status flow state machine.
    /// Appends an audit transition record to status_history.
    /// </summary>
    public static async Task<IResult> UpdateApplicationStatus(
        Guid id,
        UpdateStatusRequest req,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();

        var isRecruiterOrAdmin = role.Equals("Recruiter", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

        if (!isRecruiterOrAdmin)
            return ForbiddenResult("Only recruiters can update application statuses.");

        if (string.IsNullOrWhiteSpace(req.Status) || !Enum.TryParse<ApplicationStatus>(req.Status, true, out var newStatus))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["status"] = ["Invalid status. Allowed values: pending, reviewed, shortlisted, accepted, rejected."]
            });
        }

        if (req.Note != null && req.Note.Length > StatusHistory.NoteMaxLength)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["note"] = [$"Note exceeds maximum length of {StatusHistory.NoteMaxLength} characters."]
            });
        }

        var application = await db.Applications
            .Include(a => a.StatusHistories)
            .FirstOrDefaultAsync(a => a.Id == id);

        if (application is null)
            return Results.NotFound(new { message = "Application not found." });

        var previousStatus = application.Status;

        try
        {
            var history = application.UpdateStatus(newStatus, userId.Value, req.Note);
            db.StatusHistories.Add(history);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new
            {
                status = 409,
                message = ex.Message,
                current_status = application.Status.ToString().ToLowerInvariant(),
                allowed_transitions = application.GetAllowedTransitions().Select(s => s.ToString().ToLowerInvariant()).ToList()
            });
        }

        if (req.RecruiterNotes is not null)
        {
            application.SetRecruiterNotes(req.RecruiterNotes);
        }

        if (req.Score.HasValue)
        {
            application.SetScore(req.Score.Value);
        }

        await db.SaveChangesAsync();

        return Results.Ok(new
        {
            id = application.Id,
            message = "Trạng thái ứng tuyển đã được cập nhật thành công.",
            previous_status = previousStatus.ToString().ToLowerInvariant(),
            status = application.Status.ToString().ToLowerInvariant(),
            next_allowed_statuses = application.GetAllowedTransitions().Select(s => s.ToString().ToLowerInvariant()).ToList()
        });
    }

    /// <summary>
    /// GET /api/applications/cv/{fileName}
    /// Streams uploaded CV file for download/preview.
    /// Protected endpoint: requires applicant owner or recruiter/admin role.
    /// </summary>
    public static async Task<IResult> DownloadCv(
        string fileName,
        AppDbContext db,
        IFileStorageService storage,
        HttpContext ctx)
    {
        var (userId, role) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();

        var sanitizedFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitizedFileName))
            return Results.NotFound(new { message = "CV file not found." });

        var targetUrl = $"/api/applications/cv/{sanitizedFileName}";
        var application = await db.Applications
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.CvUrl == targetUrl || a.CvUrl.EndsWith("/" + sanitizedFileName));

        if (application is null)
            return Results.NotFound(new { message = "CV file not found." });

        var isOwner = application.ApplicantId == userId.Value;
        var isRecruiterOrAdmin = role.Equals("Recruiter", StringComparison.OrdinalIgnoreCase) ||
                                 role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

        if (!isOwner && !isRecruiterOrAdmin)
            return ForbiddenResult("You are not authorized to view or download this CV.");

        var result = await storage.GetCvAsync(sanitizedFileName);
        if (result is null)
            return Results.NotFound(new { message = "CV file not found." });

        return Results.File(
            result.Value.Stream,
            contentType: result.Value.ContentType,
            fileDownloadName: result.Value.FileName);
    }
}
