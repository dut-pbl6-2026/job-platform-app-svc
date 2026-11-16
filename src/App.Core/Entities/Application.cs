using SharedKernel;

namespace App.Core.Entities;

public class Application : Entity
{
    public const int CoverLetterMaxLength = 4000;
    public const int CvUrlMaxLength = 1024;
    public const int RecruiterNotesMaxLength = 2000;
    public const int ApplicantNameMaxLength = 200;
    public const int ApplicantEmailMaxLength = 320;

    public Guid JobId { get; private set; }
    public Guid ApplicantId { get; private set; }

    /// <summary>
    /// Candidate identity as presented in the access token when the application was submitted. Kept on
    /// the row so recruiters can address an email without reading another service. Null when an older
    /// application was submitted before these claims were forwarded.
    /// </summary>
    public string? ApplicantName { get; private set; }
    public string? ApplicantEmail { get; private set; }
    public string? CoverLetter { get; private set; }
    public string CvUrl { get; private set; } = string.Empty;
    public ApplicationStatus Status { get; private set; } = ApplicationStatus.Pending;
    public string? RecruiterNotes { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. Incremented on every change to the aggregate, so two writers that
    /// loaded the same version cannot both commit (the loser gets DbUpdateConcurrencyException).
    /// </summary>
    public int RowVersion { get; private set; }

    public ICollection<StatusHistory> StatusHistories { get; private set; } = new List<StatusHistory>();

    /// <summary>Automatic CV scoring for this application. Null only for applications created before scoring existed.</summary>
    public ScoringJob? ScoringJob { get; private set; }

    // EF Core parameterless constructor
    protected Application() { }

    public Application(
        Guid jobId,
        Guid applicantId,
        string cvUrl,
        string? coverLetter = null,
        string? applicantName = null,
        string? applicantEmail = null)
    {
        if (jobId == Guid.Empty)
            throw new ArgumentException("JobId is required.", nameof(jobId));
        if (applicantId == Guid.Empty)
            throw new ArgumentException("ApplicantId is required.", nameof(applicantId));
        if (string.IsNullOrWhiteSpace(cvUrl))
            throw new ArgumentException("CvUrl is required.", nameof(cvUrl));

        JobId = jobId;
        ApplicantId = applicantId;
        CvUrl = cvUrl.Trim();
        CoverLetter = coverLetter?.Trim();
        ApplicantName = Normalize(applicantName, ApplicantNameMaxLength);
        ApplicantEmail = Normalize(applicantEmail, ApplicantEmailMaxLength);
        Status = ApplicationStatus.Pending;

        // Record initial status in history
        StatusHistories.Add(new StatusHistory(Id, ApplicationStatus.Pending, applicantId, "Application submitted"));
    }

    public static readonly IReadOnlyDictionary<ApplicationStatus, IReadOnlyList<ApplicationStatus>> AllowedTransitions =
        new Dictionary<ApplicationStatus, IReadOnlyList<ApplicationStatus>>
        {
            [ApplicationStatus.Pending] = new[] { ApplicationStatus.Reviewed, ApplicationStatus.Rejected },
            [ApplicationStatus.Reviewed] = new[] { ApplicationStatus.Shortlisted, ApplicationStatus.Rejected },
            [ApplicationStatus.Shortlisted] = new[] { ApplicationStatus.Accepted, ApplicationStatus.Rejected },
            [ApplicationStatus.Accepted] = Array.Empty<ApplicationStatus>(),
            [ApplicationStatus.Rejected] = Array.Empty<ApplicationStatus>()
        };

    public bool CanTransitionTo(ApplicationStatus newStatus)
    {
        return AllowedTransitions.TryGetValue(Status, out var allowed) && allowed.Contains(newStatus);
    }

    private void MarkChanged()
    {
        Touch();
        RowVersion++;
    }

    public IReadOnlyList<ApplicationStatus> GetAllowedTransitions()
    {
        return AllowedTransitions.TryGetValue(Status, out var allowed) ? allowed : Array.Empty<ApplicationStatus>();
    }

    public StatusHistory UpdateStatus(ApplicationStatus newStatus, Guid changedBy, string? note = null)
    {
        if (changedBy == Guid.Empty)
            throw new ArgumentException("ChangedBy user id is required.", nameof(changedBy));

        if (Status == newStatus)
            throw new InvalidOperationException($"Application is already in '{Status}' status.");

        if (!CanTransitionTo(newStatus))
        {
            var allowed = string.Join(", ", GetAllowedTransitions());
            throw new InvalidOperationException(
                $"Invalid status transition from '{Status}' to '{newStatus}'. Allowed transitions: {(string.IsNullOrEmpty(allowed) ? "None (terminal state)" : allowed)}.");
        }

        Status = newStatus;
        MarkChanged();
        var history = new StatusHistory(Id, newStatus, changedBy, note);
        StatusHistories.Add(history);
        return history;
    }

    /// <summary>Backfills the candidate identity on an application submitted before it was captured.</summary>
    public void SetApplicantIdentity(string? name, string? email)
    {
        ApplicantName = Normalize(name, ApplicantNameMaxLength);
        ApplicantEmail = Normalize(email, ApplicantEmailMaxLength);
    }

    private static string? Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    public void SetRecruiterNotes(string? notes)
    {
        if (notes != null && notes.Length > RecruiterNotesMaxLength)
            throw new ArgumentException($"Recruiter notes cannot exceed {RecruiterNotesMaxLength} characters.", nameof(notes));

        RecruiterNotes = notes?.Trim();
        MarkChanged();
    }
}
