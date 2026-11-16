using System.Net;
using System.Security.Claims;
using App.Api.Services;
using App.Core.DTOs;
using App.Core.Entities;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace App.Api.Endpoints;

/// <summary>
/// Recruiter engagement: saved email templates, sending an email to one applicant, and interview
/// scheduling with an iCalendar invite. Templates are private to their author; every application-bound
/// action repeats the job-ownership check app-svc uses for status changes.
/// </summary>
public static class EngagementEndpoints
{
    private const int SubjectMaxLength = 200;
    private const int EmailBodyMaxLength = 8000;
    private const int IcsMaxBytes = 256 * 1024;

    public static WebApplication MapEngagementEndpoints(this WebApplication app)
    {
        var templates = app.MapGroup("/api/email-templates").WithTags("Email templates");
        templates.MapGet("/", ListTemplates);
        templates.MapPost("/", CreateTemplate);
        templates.MapPut("/{id:guid}", UpdateTemplate);
        templates.MapDelete("/{id:guid}", DeleteTemplate);

        var interviews = app.MapGroup("/api/interviews").WithTags("Interviews");
        interviews.MapGet("/", ListInterviews);
        interviews.MapPost("/", CreateInterview);
        interviews.MapGet("/{id:guid}/ics", DownloadIcs);
        interviews.MapPost("/{id:guid}/cancel", CancelInterview);
        interviews.MapPut("/{id:guid}", RescheduleInterview);
        interviews.MapGet("/{id:guid}", GetInterview);

        app.MapPost("/api/applications/{id:guid}/email", SendApplicationEmail)
            .WithTags("Application email");

        return app;
    }

    // ── templates ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListTemplates(AppDbContext db, HttpContext ctx)
    {
        var (userId, role) = ApplicationEndpoints.GetIdentity(ctx);
        if (userId is null)
            return Unauthorized();
        if (!IsRecruiter(role))
            return Forbidden("Only recruiters can manage email templates.");

        var items = await db.EmailTemplates
            .AsNoTracking()
            .Where(t => t.RecruiterId == userId.Value)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new EmailTemplateDto(t.Id, t.Name, t.Subject, t.Body, t.CreatedAt, t.UpdatedAt))
            .ToListAsync();

        return Results.Ok(items);
    }

    private static async Task<IResult> CreateTemplate(
        EmailTemplateRequest request,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = ApplicationEndpoints.GetIdentity(ctx);
        if (userId is null)
            return Unauthorized();
        if (!IsRecruiter(role))
            return Forbidden("Only recruiters can manage email templates.");

        var errors = ValidateTemplate(request);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        EmailTemplate template;
        try
        {
            template = new EmailTemplate(userId.Value, request.Name, request.Subject, request.Body);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["template"] = [ex.Message] });
        }

        db.EmailTemplates.Add(template);
        await db.SaveChangesAsync();

        return Results.Created(
            $"/api/email-templates/{template.Id}",
            new EmailTemplateDto(template.Id, template.Name, template.Subject, template.Body, template.CreatedAt, template.UpdatedAt));
    }

    private static async Task<IResult> UpdateTemplate(
        Guid id,
        EmailTemplateRequest request,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = ApplicationEndpoints.GetIdentity(ctx);
        if (userId is null)
            return Unauthorized();
        if (!IsRecruiter(role))
            return Forbidden("Only recruiters can manage email templates.");

        var errors = ValidateTemplate(request);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var template = await db.EmailTemplates.FirstOrDefaultAsync(t => t.Id == id && t.RecruiterId == userId.Value);
        if (template is null)
            return Results.NotFound(new { message = "Email template not found." });

        try
        {
            template.Update(request.Name, request.Subject, request.Body);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["template"] = [ex.Message] });
        }

        await db.SaveChangesAsync();
        return Results.Ok(new EmailTemplateDto(
            template.Id, template.Name, template.Subject, template.Body, template.CreatedAt, template.UpdatedAt));
    }

    private static async Task<IResult> DeleteTemplate(Guid id, AppDbContext db, HttpContext ctx)
    {
        var (userId, role) = ApplicationEndpoints.GetIdentity(ctx);
        if (userId is null)
            return Unauthorized();
        if (!IsRecruiter(role))
            return Forbidden("Only recruiters can manage email templates.");

        var template = await db.EmailTemplates.FirstOrDefaultAsync(t => t.Id == id && t.RecruiterId == userId.Value);
        if (template is null)
            return Results.NotFound(new { message = "Email template not found." });

        db.EmailTemplates.Remove(template);
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static Dictionary<string, string[]> ValidateTemplate(EmailTemplateRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name))
            errors["name"] = ["Name is required."];
        else if (request.Name.Trim().Length > EmailTemplate.NameMaxLength)
            errors["name"] = [$"Name cannot exceed {EmailTemplate.NameMaxLength} characters."];

        if (string.IsNullOrWhiteSpace(request.Subject))
            errors["subject"] = ["Subject is required."];
        else if (request.Subject.Trim().Length > EmailTemplate.SubjectMaxLength)
            errors["subject"] = [$"Subject cannot exceed {EmailTemplate.SubjectMaxLength} characters."];

        if (string.IsNullOrWhiteSpace(request.Body))
            errors["body"] = ["Body is required."];
        else if (request.Body.Length > EmailTemplate.BodyMaxLength)
            errors["body"] = [$"Body cannot exceed {EmailTemplate.BodyMaxLength} characters."];

        return errors;
    }

    // ── applications: send an email ──────────────────────────────────────────────────────────────

    private static async Task<IResult> SendApplicationEmail(
        Guid id,
        SendApplicationEmailRequest request,
        AppDbContext db,
        HttpContext ctx,
        NotifEmailClient? email,
        JobServiceClient? jobClient = null)
    {
        var (userId, role) = ApplicationEndpoints.GetIdentity(ctx);
        if (userId is null)
            return Unauthorized();
        if (!IsRecruiterOrAdmin(role))
            return Forbidden("Only recruiters can email an applicant.");

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Subject))
            errors["subject"] = ["Subject is required."];
        else if (request.Subject.Trim().Length > SubjectMaxLength)
            errors["subject"] = [$"Subject cannot exceed {SubjectMaxLength} characters."];

        if (string.IsNullOrWhiteSpace(request.Body))
            errors["body"] = ["Body is required."];
        else if (request.Body.Length > EmailBodyMaxLength)
            errors["body"] = [$"Body cannot exceed {EmailBodyMaxLength} characters."];

        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var application = await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
        if (application is null)
            return Results.NotFound(new { message = "Application not found." });

        var (denied, job) = await ApplicationEndpoints.ResolveOwnedJobAsync(ctx, role, application.JobId, jobClient);
        if (denied is not null)
            return denied;

        if (string.IsNullOrWhiteSpace(application.ApplicantEmail))
            return Results.Conflict(new { message = "The applicant has no email address on record." });

        if (email is null || !email.Enabled)
            return Results.Problem(
                detail: "Email delivery is not configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable);

        var recruiterName = GetDisplayName(ctx);
        var jobs = new Dictionary<string, string>
        {
            ["candidate_name"] = application.ApplicantName ?? "bạn",
            ["job_title"] = job?.Title ?? string.Empty,
            ["recruiter_name"] = recruiterName ?? string.Empty,
        };
        var subject = ReplacePlaceholders(request.Subject.Trim(), jobs);
        var body = ReplacePlaceholders(request.Body.Trim(), jobs);

        var dispatch = new EmailDispatch(
            application.ApplicantEmail!,
            subject,
            ToHtml(body),
            body);

        var result = await email.SendAsync(dispatch, ctx.RequestAborted);
        if (!result.Sent)
            return Results.Problem(
                detail: result.Error ?? "The mail service could not send the message.",
                statusCode: StatusCodes.Status502BadGateway);

        return Results.Ok(new EmailSendResult(true, application.ApplicantEmail!));
    }

    // ── interviews ───────────────────────────────────────────────────────────────────────────────

    private static async Task<IResult> ListInterviews(
        DateTime? from,
        DateTime? to,
        Guid? jobId,
        AppDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = GetInterviewIdentity(ctx);
        if (userId is null)
            return Unauthorized();

        var query = db.Interviews.AsNoTracking();

        if (role.Equals("Recruiter", StringComparison.OrdinalIgnoreCase))
            query = query.Where(i => i.RecruiterId == userId.Value);
        else if (!role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            query = query.Where(i => i.ApplicantId == userId.Value);

        if (from.HasValue)
        {
            var start = ToUtc(from.Value);
            query = query.Where(i => i.StartsAtUtc >= start);
        }

        if (to.HasValue)
        {
            var end = ToUtc(to.Value);
            query = query.Where(i => i.StartsAtUtc < end);
        }

        if (jobId.HasValue && jobId.Value != Guid.Empty)
            query = query.Where(i => i.JobId == jobId.Value);

        var rows = await query
            .OrderBy(i => i.StartsAtUtc)
            .Take(500)
            .ToListAsync();

        return Results.Ok(rows.Select(i => i.ToDto()).ToList());
    }

    private static async Task<IResult> GetInterview(Guid id, AppDbContext db, HttpContext ctx)
    {
        var (userId, role) = GetInterviewIdentity(ctx);
        if (userId is null)
            return Unauthorized();

        var interview = await db.Interviews.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (interview is null)
            return Results.NotFound(new { message = "Interview not found." });

        if (!CanSeeInterview(interview, userId.Value, role))
            return Forbidden("You are not a participant in this interview.");

        return Results.Ok(interview.ToDto());
    }

    private static async Task<IResult> CreateInterview(
        CreateInterviewRequest request,
        AppDbContext db,
        HttpContext ctx,
        JobServiceClient? jobClient,
        NotifEmailClient? email)
    {
        var (userId, role) = GetInterviewIdentity(ctx);
        if (userId is null)
            return Unauthorized();
        if (!IsRecruiterOrAdmin(role))
            return Forbidden("Only recruiters can schedule interviews.");

        var errors = ValidateSchedule(request.StartsAtUtc, request.DurationMinutes, request.Mode);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var application = await db.Applications.FirstOrDefaultAsync(a => a.Id == request.ApplicationId);
        if (application is null)
            return Results.NotFound(new { message = "Application not found." });

        var (denied, job) = await ApplicationEndpoints.ResolveOwnedJobAsync(ctx, role, application.JobId, jobClient);
        if (denied is not null)
            return denied;

        if (string.IsNullOrWhiteSpace(application.ApplicantEmail))
        {
            return Results.Conflict(new
            {
                message = "The applicant has no email address on record, so an invite cannot be sent.",
            });
        }

        var recruiterEmail = GetEmail(ctx);
        if (string.IsNullOrWhiteSpace(recruiterEmail))
            return Results.Conflict(new { message = "Your account has no email address on record." });

        Interview interview;
        try
        {
            interview = new Interview(
                application.Id,
                application.JobId,
                job?.Title ?? string.Empty,
                job?.CompanyId ?? Guid.Empty,
                application.ApplicantId,
                application.ApplicantName,
                application.ApplicantEmail!,
                userId.Value,
                GetDisplayName(ctx),
                recruiterEmail!,
                request.StartsAtUtc,
                request.DurationMinutes,
                ParseMode(request.Mode),
                request.Location,
                request.Notes);
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["interview"] = [ex.Message] });
        }

        db.Interviews.Add(interview);
        await db.SaveChangesAsync();

        var inviteSent = false;
        string? inviteError = null;
        if (request.SendInvite)
        {
            var result = await SendInterviewMailAsync(email, interview, invite: true, ctx.RequestAborted);
            inviteSent = result.Sent;
            inviteError = result.Error;
        }

        return Results.Created(
            $"/api/interviews/{interview.Id}",
            new InterviewCreatedDto(interview.ToDto(), inviteSent, inviteError));
    }

    private static async Task<IResult> RescheduleInterview(
        Guid id,
        RescheduleInterviewRequest request,
        AppDbContext db,
        HttpContext ctx,
        NotifEmailClient? email)
    {
        var (userId, role) = GetInterviewIdentity(ctx);
        if (userId is null)
            return Unauthorized();

        var interview = await db.Interviews.FirstOrDefaultAsync(i => i.Id == id);
        if (interview is null)
            return Results.NotFound(new { message = "Interview not found." });
        if (!IsRecruiterOrAdmin(role) || interview.RecruiterId != userId.Value && !role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            return Forbidden("Only the recruiter who scheduled this interview can change it.");

        var errors = ValidateSchedule(request.StartsAtUtc, request.DurationMinutes, interview.Mode.ToString());
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        try
        {
            interview.Reschedule(request.StartsAtUtc, request.DurationMinutes);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }

        await db.SaveChangesAsync();

        var sent = false;
        string? error = null;
        if (request.SendUpdate)
        {
            var result = await SendInterviewMailAsync(email, interview, invite: true, ctx.RequestAborted);
            sent = result.Sent;
            error = result.Error;
        }

        return Results.Ok(new InterviewCreatedDto(interview.ToDto(), sent, error));
    }

    private static async Task<IResult> CancelInterview(
        Guid id,
        CancelInterviewRequest? request,
        AppDbContext db,
        HttpContext ctx,
        NotifEmailClient? email)
    {
        var (userId, role) = GetInterviewIdentity(ctx);
        if (userId is null)
            return Unauthorized();

        var interview = await db.Interviews.FirstOrDefaultAsync(i => i.Id == id);
        if (interview is null)
            return Results.NotFound(new { message = "Interview not found." });
        if (!IsRecruiterOrAdmin(role) || interview.RecruiterId != userId.Value && !role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
            return Forbidden("Only the recruiter who scheduled this interview can cancel it.");

        try
        {
            interview.Cancel();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }

        await db.SaveChangesAsync();

        var sent = false;
        string? error = null;
        if (request?.SendUpdate ?? true)
        {
            var result = await SendInterviewMailAsync(email, interview, invite: false, ctx.RequestAborted);
            sent = result.Sent;
            error = result.Error;
        }

        return Results.Ok(new InterviewCreatedDto(interview.ToDto(), sent, error));
    }

    /// <summary>GET /api/interviews/{id}/ics — participants download the event file directly.</summary>
    private static async Task<IResult> DownloadIcs(Guid id, AppDbContext db, HttpContext ctx)
    {
        var (userId, role) = GetInterviewIdentity(ctx);
        if (userId is null)
            return Unauthorized();

        var interview = await db.Interviews.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (interview is null)
            return Results.NotFound(new { message = "Interview not found." });

        if (!CanSeeInterview(interview, userId.Value, role))
            return Forbidden("You are not a participant in this interview.");

        var content = interview.Status == InterviewStatus.Cancelled
            ? IcsBuilder.BuildCancellation(interview)
            : IcsBuilder.BuildInvite(interview);

        var safeTitle = new string(interview.JobTitle
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .Take(60)
            .ToArray());
        var fileName = $"interview-{(safeTitle.Length > 0 ? safeTitle : interview.Id.ToString())}.ics";

        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(content),
            contentType: "text/calendar; charset=utf-8",
            fileDownloadName: fileName);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static async Task<EmailDispatchResult> SendInterviewMailAsync(
        NotifEmailClient? email,
        Interview interview,
        bool invite,
        CancellationToken ct)
    {
        if (email is null || !email.Enabled)
            return EmailDispatchResult.Failed("Email delivery is not configured.");

        var when = interview.StartsAtUtc.ToString("dd/MM/yyyy HH:mm 'UTC'");
        var subject = invite
            ? $"Thư mời phỏng vấn: {interview.JobTitle}"
            : $"Huỷ lịch phỏng vấn: {interview.JobTitle}";
        var greeting = string.IsNullOrWhiteSpace(interview.ApplicantName)
            ? "Xin chào,"
            : $"Xin chào {interview.ApplicantName},";

        var lines = new List<string>
        {
            greeting,
            invite
                ? "Chúng tôi trân trọng mời bạn tham dự buổi phỏng vấn cho vị trí dưới đây."
                : "Buổi phỏng vấn dưới đây đã được huỷ. Vui lòng liên hệ nhà tuyển dụng nếu bạn cần sắp xếp lại.",
            $"Vị trí: {interview.JobTitle}",
            $"Thời gian: {when} (dự kiến {interview.DurationMinutes} phút)",
            $"Hình thức: {ModeLabel(interview.Mode)}",
        };
        if (!string.IsNullOrWhiteSpace(interview.Location))
            lines.Add($"Địa điểm / liên kết: {interview.Location}");
        if (!string.IsNullOrWhiteSpace(interview.Notes))
            lines.Add($"Ghi chú: {interview.Notes}");
        lines.Add("Tệp đính kèm .ics có thể thêm trực tiếp vào lịch của bạn.");
        lines.Add(string.IsNullOrWhiteSpace(interview.RecruiterName)
            ? interview.RecruiterEmail
            : $"{interview.RecruiterName} ({interview.RecruiterEmail})");

        var text = string.Join("\n", lines);
        var ics = invite
            ? IcsBuilder.BuildInvite(interview)
            : IcsBuilder.BuildCancellation(interview);

        if (System.Text.Encoding.UTF8.GetByteCount(ics) > IcsMaxBytes)
            return EmailDispatchResult.Failed("The calendar attachment is too large.");

        var dispatch = new EmailDispatch(
            interview.ApplicantEmail,
            subject,
            ToHtml(text),
            text,
            $"invite-{interview.Id}.ics",
            ics);

        return await email.SendAsync(dispatch, ct);
    }

    private static bool CanSeeInterview(Interview interview, Guid userId, string role) =>
        role.Equals("Admin", StringComparison.OrdinalIgnoreCase)
        || interview.ApplicantId == userId
        || interview.RecruiterId == userId;

    private static (Guid? UserId, string Role) GetInterviewIdentity(HttpContext ctx) =>
        ApplicationEndpoints.GetIdentity(ctx);

    private static string? GetEmail(HttpContext ctx) =>
        ctx.User.FindFirst(ClaimTypes.Email)?.Value ?? ctx.User.FindFirst("email")?.Value;

    private static string? GetDisplayName(HttpContext ctx) =>
        ctx.User.FindFirst("full_name")?.Value
        ?? ctx.User.FindFirst(ClaimTypes.Name)?.Value
        ?? ctx.User.FindFirst("name")?.Value;

    private static bool IsRecruiter(string role) => role.Equals("Recruiter", StringComparison.OrdinalIgnoreCase);

    private static bool IsRecruiterOrAdmin(string role) =>
        IsRecruiter(role) || role.Equals("Admin", StringComparison.OrdinalIgnoreCase);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static InterviewMode ParseMode(string mode) =>
        Enum.TryParse<InterviewMode>(mode, ignoreCase: true, out var parsed) ? parsed : InterviewMode.Online;

    private static string ModeLabel(InterviewMode mode) => mode switch
    {
        InterviewMode.Online => "Trực tuyến",
        InterviewMode.Onsite => "Tại văn phòng",
        _ => "Điện thoại",
    };

    private static Dictionary<string, string[]> ValidateSchedule(DateTime startsAtUtc, int durationMinutes, string mode)
    {
        var errors = new Dictionary<string, string[]>();
        var start = ToUtc(startsAtUtc);

        if (start <= DateTime.UtcNow.AddMinutes(-5))
            errors["startsAtUtc"] = ["The interview must be scheduled in the future."];
        if (start > DateTime.UtcNow.AddYears(2))
            errors["startsAtUtc"] = ["The interview is too far in the future."];

        if (durationMinutes is < Interview.MinDurationMinutes or > Interview.MaxDurationMinutes)
        {
            errors["durationMinutes"] =
                [$"Duration must be between {Interview.MinDurationMinutes} and {Interview.MaxDurationMinutes} minutes."];
        }

        if (!Enum.TryParse<InterviewMode>(mode, ignoreCase: true, out _))
            errors["mode"] = ["Mode must be online, onsite or phone."];

        return errors;
    }

    private static string ReplacePlaceholders(string value, IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, replacement) in values)
            value = value.Replace("{{" + key + "}}", replacement, StringComparison.OrdinalIgnoreCase);

        return value;
    }

    /// <summary>Turns a plain-text body into a small HTML document; every line is encoded, never trusted.</summary>
    private static string ToHtml(string text)
    {
        var paragraphs = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block => $"<p style=\"margin:0 0 12px\">{WebUtility.HtmlEncode(block).Replace("\n", "<br />")}</p>");

        return "<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:15px;line-height:1.55;color:#1b2430\">"
               + string.Concat(paragraphs)
               + "</div>";
    }

    private static IResult Unauthorized() => Results.Json(new { message = "Unauthorized." }, statusCode: 401);

    private static IResult Forbidden(string message) => Results.Json(new { message }, statusCode: 403);
}
