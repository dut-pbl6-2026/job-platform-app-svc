using SharedKernel;

namespace App.Core.Entities;

public class StatusHistory : Entity
{
    public const int NoteMaxLength = 1000;

    public Guid ApplicationId { get; private set; }
    public ApplicationStatus Status { get; private set; }
    public string? Note { get; private set; }
    public Guid ChangedBy { get; private set; }
    public DateTime ChangedAt { get; private set; } = DateTime.UtcNow;

    public Application? Application { get; private set; }

    // EF Core constructor
    protected StatusHistory() { }

    public StatusHistory(Guid applicationId, ApplicationStatus status, Guid changedBy, string? note = null)
    {
        if (applicationId == Guid.Empty)
            throw new ArgumentException("ApplicationId is required.", nameof(applicationId));
        if (changedBy == Guid.Empty)
            throw new ArgumentException("ChangedBy is required.", nameof(changedBy));

        var trimmedNote = note?.Trim();
        if (trimmedNote != null && trimmedNote.Length > NoteMaxLength)
            throw new ArgumentException($"Note cannot exceed {NoteMaxLength} characters.", nameof(note));

        ApplicationId = applicationId;
        Status = status;
        ChangedBy = changedBy;
        Note = trimmedNote;
        ChangedAt = DateTime.UtcNow;
    }
}
