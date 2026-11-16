using App.Core.Entities;

namespace App.Core.DTOs;

/// <summary>A recruiter's saved email template. Private to its author.</summary>
public record EmailTemplateDto(
    Guid Id,
    string Name,
    string Subject,
    string Body,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record EmailTemplateRequest(string Name, string Subject, string Body);

/// <summary>Body for POST /api/applications/{id}/email. Null reply-to uses the recruiter's own address.</summary>
public record SendApplicationEmailRequest(string Subject, string Body);

public record EmailSendResult(bool Sent, string Recipient, string? Error = null);

public record InterviewDto(
    Guid Id,
    Guid ApplicationId,
    Guid JobId,
    string JobTitle,
    Guid CompanyId,
    Guid ApplicantId,
    string? ApplicantName,
    string ApplicantEmail,
    Guid RecruiterId,
    string? RecruiterName,
    string RecruiterEmail,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    int DurationMinutes,
    string Mode,
    string? Location,
    string? Notes,
    string Status,
    string CalendarUid,
    DateTime CreatedAt);

/// <summary>
/// Creates an interview for an application. Times are UTC; the API also accepts an offset and stores
/// the equivalent UTC instant. <see cref="SendInvite"/> false stores the interview without mailing.
/// </summary>
public record CreateInterviewRequest(
    Guid ApplicationId,
    DateTime StartsAtUtc,
    int DurationMinutes,
    string Mode,
    string? Location = null,
    string? Notes = null,
    bool SendInvite = true);

public record RescheduleInterviewRequest(
    DateTime StartsAtUtc,
    int DurationMinutes,
    bool SendUpdate = true);

public record CancelInterviewRequest(bool SendUpdate = true);

public record InterviewCreatedDto(InterviewDto Interview, bool InviteSent, string? InviteError = null);

public static class InterviewMapping
{
    public static InterviewDto ToDto(this Interview interview) => new(
        interview.Id,
        interview.ApplicationId,
        interview.JobId,
        interview.JobTitle,
        interview.CompanyId,
        interview.ApplicantId,
        interview.ApplicantName,
        interview.ApplicantEmail,
        interview.RecruiterId,
        interview.RecruiterName,
        interview.RecruiterEmail,
        interview.StartsAtUtc,
        interview.EndsAtUtc,
        interview.DurationMinutes,
        interview.Mode.ToString().ToLowerInvariant(),
        interview.Location,
        interview.Notes,
        interview.Status.ToString().ToLowerInvariant(),
        interview.CalendarUid,
        interview.CreatedAt);
}
