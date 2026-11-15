namespace App.Core.Entities;

/// <summary>
/// State of the automatic CV-to-job scoring of one application. <c>Pending</c> rows are claimed by the
/// scoring worker; <c>Done</c> and <c>Failed</c> are terminal.
/// </summary>
public enum ScoringStatus
{
    Pending,
    Done,
    Failed
}
