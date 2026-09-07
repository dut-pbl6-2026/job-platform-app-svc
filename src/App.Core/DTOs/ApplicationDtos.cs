using App.Core.Entities;

namespace App.Core.DTOs;

public record ApplyJobRequest(Guid JobId, string? CoverLetter);

public record ApplicationDetailDto(
    Guid Id,
    Guid JobId,
    Guid ApplicantId,
    string? CoverLetter,
    string CvUrl,
    string Status,
    string? RecruiterNotes,
    double? Score,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<StatusHistoryDto> StatusHistory
);

public record ApplicationSummaryDto(
    Guid Id,
    Guid JobId,
    Guid ApplicantId,
    string Status,
    string CvUrl,
    string? CoverLetter,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record StatusHistoryDto(
    Guid Id,
    string Status,
    string? Note,
    Guid ChangedBy,
    DateTime ChangedAt
);

public record UpdateStatusRequest(
    string Status,
    string? Note
);

public record PaginatedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int Size
);
