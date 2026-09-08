using System.Text.Json.Serialization;
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
    string? RecruiterNotes = null,
    double? Score = null
);

public record StatusFlowDto(
<<<<<<< HEAD
    [property: JsonPropertyName("all_statuses")] IReadOnlyList<string> AllStatuses,
    [property: JsonPropertyName("terminal_statuses")] IReadOnlyList<string> TerminalStatuses,
    [property: JsonPropertyName("transitions")] IReadOnlyDictionary<string, IReadOnlyList<string>> Transitions
=======
    IReadOnlyList<string> AllStatuses,
    IReadOnlyList<string> TerminalStatuses,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Transitions
>>>>>>> 51cf00ad85077f8148705bed9cb84ef939879c08
);

public record PaginatedResponse<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int Size
);
