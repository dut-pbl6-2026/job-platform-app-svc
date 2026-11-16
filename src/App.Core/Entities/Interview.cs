using SharedKernel;

namespace App.Core.Entities;

public enum InterviewMode
{
    Online,
    Onsite,
    Phone,
}

public enum InterviewStatus
{
    Scheduled,
    Cancelled,
}

/// <summary>
/// One scheduled interview for an application. The row snapshots the job title, the recruiter's
/// address and the candidate's identity so a calendar can render and an invite can be rebuilt without
/// joining other services or depending on data that may change later.
/// </summary>
public class Interview : Entity
{
    public const int LocationMaxLength = 300;
    public const int NotesMaxLength = 2000;
    public const int JobTitleMaxLength = 300;
    public const int NameMaxLength = 200;
    public const int EmailMaxLength = 320;
    public const int UidMaxLength = 200;

    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 480;

    public Guid ApplicationId { get; private set; }
    public Guid JobId { get; private set; }
    public string JobTitle { get; private set; } = string.Empty;
    public Guid CompanyId { get; private set; }

    public Guid ApplicantId { get; private set; }
    public string? ApplicantName { get; private set; }
    public string ApplicantEmail { get; private set; } = string.Empty;

    public Guid RecruiterId { get; private set; }
    public string? RecruiterName { get; private set; }
    public string RecruiterEmail { get; private set; } = string.Empty;

    /// <summary>Start instant in UTC. Calendars convert it to the viewer's zone.</summary>
    public DateTime StartsAtUtc { get; private set; }
    public int DurationMinutes { get; private set; }
    public InterviewMode Mode { get; private set; }
    public string? Location { get; private set; }
    public string? Notes { get; private set; }
    public InterviewStatus Status { get; private set; } = InterviewStatus.Scheduled;

    /// <summary>Stable UID shared by the invite and the cancellation, as RFC 5545 requires.</summary>
    public string CalendarUid { get; private set; } = string.Empty;

    // EF Core constructor
    protected Interview() { }

    public Interview(
        Guid applicationId,
        Guid jobId,
        string jobTitle,
        Guid companyId,
        Guid applicantId,
        string? applicantName,
        string applicantEmail,
        Guid recruiterId,
        string? recruiterName,
        string recruiterEmail,
        DateTime startsAtUtc,
        int durationMinutes,
        InterviewMode mode,
        string? location,
        string? notes)
    {
        if (applicationId == Guid.Empty)
            throw new ArgumentException("ApplicationId is required.", nameof(applicationId));
        if (jobId == Guid.Empty)
            throw new ArgumentException("JobId is required.", nameof(jobId));
        if (applicantId == Guid.Empty)
            throw new ArgumentException("ApplicantId is required.", nameof(applicantId));
        if (recruiterId == Guid.Empty)
            throw new ArgumentException("RecruiterId is required.", nameof(recruiterId));
        if (string.IsNullOrWhiteSpace(applicantEmail))
            throw new ArgumentException("The candidate has no email address on record.", nameof(applicantEmail));
        if (string.IsNullOrWhiteSpace(recruiterEmail))
            throw new ArgumentException("RecruiterEmail is required.", nameof(recruiterEmail));
        if (durationMinutes is < MinDurationMinutes or > MaxDurationMinutes)
            throw new ArgumentOutOfRangeException(
                nameof(durationMinutes),
                $"Duration must be between {MinDurationMinutes} and {MaxDurationMinutes} minutes.");

        ApplicationId = applicationId;
        JobId = jobId;
        JobTitle = TrimTo(jobTitle, JobTitleMaxLength) ?? string.Empty;
        CompanyId = companyId;
        ApplicantId = applicantId;
        ApplicantName = TrimTo(applicantName, NameMaxLength);
        ApplicantEmail = applicantEmail.Trim();
        RecruiterId = recruiterId;
        RecruiterName = TrimTo(recruiterName, NameMaxLength);
        RecruiterEmail = recruiterEmail.Trim();
        StartsAtUtc = NormalizeUtc(startsAtUtc);
        DurationMinutes = durationMinutes;
        Mode = mode;
        Location = TrimTo(location, LocationMaxLength);
        Notes = TrimTo(notes, NotesMaxLength);
        Status = InterviewStatus.Scheduled;
        CalendarUid = Id.ToString();
    }

    public DateTime EndsAtUtc => StartsAtUtc.AddMinutes(DurationMinutes);

    public void Cancel()
    {
        if (Status == InterviewStatus.Cancelled)
            throw new InvalidOperationException("The interview is already cancelled.");

        Status = InterviewStatus.Cancelled;
        Touch();
    }

    public void Reschedule(DateTime startsAtUtc, int durationMinutes)
    {
        if (Status == InterviewStatus.Cancelled)
            throw new InvalidOperationException("A cancelled interview cannot be rescheduled.");
        if (durationMinutes is < MinDurationMinutes or > MaxDurationMinutes)
            throw new ArgumentOutOfRangeException(nameof(durationMinutes));

        StartsAtUtc = NormalizeUtc(startsAtUtc);
        DurationMinutes = durationMinutes;
        Touch();
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static string? TrimTo(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
