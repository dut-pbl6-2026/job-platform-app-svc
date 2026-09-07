using SharedKernel;

namespace App.Core.Entities;

public class Application : Entity
{
    public const int CoverLetterMaxLength = 4000;
    public const int CvUrlMaxLength = 1024;
    public const int RecruiterNotesMaxLength = 2000;

    public Guid JobId { get; private set; }
    public Guid ApplicantId { get; private set; }
    public string? CoverLetter { get; private set; }
    public string CvUrl { get; private set; } = string.Empty;
    public ApplicationStatus Status { get; private set; } = ApplicationStatus.Pending;
    public string? RecruiterNotes { get; private set; }
    public double? Score { get; private set; }

    public ICollection<StatusHistory> StatusHistories { get; private set; } = new List<StatusHistory>();

    // EF Core parameterless constructor
    protected Application() { }

    public Application(Guid jobId, Guid applicantId, string cvUrl, string? coverLetter = null)
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
        Status = ApplicationStatus.Pending;

        // Record initial status in history
        StatusHistories.Add(new StatusHistory(Id, ApplicationStatus.Pending, applicantId, "Application submitted"));
    }

    public static readonly IReadOnlyDictionary<ApplicationStatus, IReadOnlyList<ApplicationStatus>> AllowedTransitions =
        new Dictionary<ApplicationStatus, IReadOnlyList<ApplicationStatus>>
        {
            [ApplicationStatus.Pending] = new[] { ApplicationStatus.Reviewed, ApplicationStatus.Shortlisted, ApplicationStatus.Rejected },
            [ApplicationStatus.Reviewed] = new[] { ApplicationStatus.Shortlisted, ApplicationStatus.Rejected },
            [ApplicationStatus.Shortlisted] = new[] { ApplicationStatus.Accepted, ApplicationStatus.Rejected },
            [ApplicationStatus.Accepted] = Array.Empty<ApplicationStatus>(),
            [ApplicationStatus.Rejected] = Array.Empty<ApplicationStatus>()
        };

    public bool CanTransitionTo(ApplicationStatus newStatus)
    {
        return AllowedTransitions.TryGetValue(Status, out var allowed) && allowed.Contains(newStatus);
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
        Touch();
        var history = new StatusHistory(Id, newStatus, changedBy, note);
        StatusHistories.Add(history);
        return history;
    }

    public void SetRecruiterNotes(string? notes)
    {
        RecruiterNotes = notes?.Trim();
        Touch();
    }

    public void SetScore(double? score)
    {
        Score = score;
        Touch();
    }
}
