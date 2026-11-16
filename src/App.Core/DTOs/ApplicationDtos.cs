using System.Text.Json.Serialization;
using App.Core.Entities;

namespace App.Core.DTOs;

public record ApplyJobRequest(Guid JobId, string? CoverLetter);

/// <summary>
/// Detail view for the applicant who submitted the application. Never includes recruiter notes, the match
/// score or its status: scoring is visible to recruiters only.
/// </summary>
public record ApplicantApplicationDetailDto(
    Guid Id,
    Guid JobId,
    Guid ApplicantId,
    string? CoverLetter,
    string CvUrl,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<StatusHistoryDto> StatusHistory,
    IReadOnlyList<string>? NextAllowedStatuses = null
);

/// <summary>Detail view for recruiters and admins: the applicant view plus internal recruiter notes.</summary>
public record RecruiterApplicationDetailDto(
    Guid Id,
    Guid JobId,
    Guid ApplicantId,
    string? CoverLetter,
    string CvUrl,
    string Status,
    string? RecruiterNotes,
    double? Score,
    string? ScoreStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<StatusHistoryDto> StatusHistory,
    IReadOnlyList<string>? NextAllowedStatuses = null
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

/// <summary>Recruiter row for the applications of one job: the summary plus the automatic match score.</summary>
public record RecruiterApplicationSummaryDto(
    Guid Id,
    Guid JobId,
    Guid ApplicantId,
    string Status,
    string CvUrl,
    string? CoverLetter,
    double? Score,
    string? ScoreStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? ApplicantName = null,
    string? ApplicantEmail = null
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
    string? Note = null,
    string? RecruiterNotes = null
);

public record StatusFlowDto(
    [property: JsonPropertyName("all_statuses")] IReadOnlyList<string> AllStatuses,
    [property: JsonPropertyName("terminal_statuses")] IReadOnlyList<string> TerminalStatuses,
    [property: JsonPropertyName("transitions")] IReadOnlyDictionary<string, IReadOnlyList<string>> Transitions
);

public record PaginatedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int Size
);
