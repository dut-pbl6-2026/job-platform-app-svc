using System.Text;
using App.Core.Entities;

namespace App.Api.Services;

/// <summary>
/// RFC 5545 iCalendar output for interview invites and cancellations. One event per file, UTC times,
/// a stable UID and a METHOD the mail client understands. Lines are folded at 75 octets as the RFC
/// requires, so long Vietnamese descriptions survive every parser.
/// </summary>
public static class IcsBuilder
{
    private const string ProductId = "-//JobPlatform//Interview//VI";
    private const string DateFormat = "yyyyMMdd'T'HHmmss'Z'";

    public static string BuildInvite(Interview interview) =>
        Build(interview, method: "REQUEST", status: "CONFIRMED", sequence: 0);

    public static string BuildCancellation(Interview interview) =>
        Build(interview, method: "CANCEL", status: "CANCELLED", sequence: 1);

    private static string Build(Interview interview, string method, string status, int sequence)
    {
        var description = BuildDescription(interview);
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            $"PRODID:{ProductId}",
            "CALSCALE:GREGORIAN",
            $"METHOD:{method}",
            "BEGIN:VEVENT",
            $"UID:{Escape(interview.CalendarUid)}",
            $"DTSTAMP:{DateTime.UtcNow.ToString(DateFormat)}",
            $"DTSTART:{interview.StartsAtUtc.ToString(DateFormat)}",
            $"DTEND:{interview.EndsAtUtc.ToString(DateFormat)}",
            $"SUMMARY:{Escape($"Phỏng vấn: {interview.JobTitle}")}",
            $"DESCRIPTION:{Escape(description)}",
        };

        if (!string.IsNullOrWhiteSpace(interview.Location))
            lines.Add($"LOCATION:{Escape(interview.Location)}");

        var organizerName = string.IsNullOrWhiteSpace(interview.RecruiterName)
            ? interview.RecruiterEmail
            : interview.RecruiterName;
        lines.Add($"ORGANIZER;CN={Escape(organizerName)}:mailto:{interview.RecruiterEmail}");

        var attendeeName = string.IsNullOrWhiteSpace(interview.ApplicantName)
            ? interview.ApplicantEmail
            : interview.ApplicantName;
        lines.Add(
            $"ATTENDEE;CN={Escape(attendeeName)};ROLE=REQ-PARTICIPANT;RSVP=TRUE:mailto:{interview.ApplicantEmail}");

        lines.Add($"STATUS:{status}");
        lines.Add($"SEQUENCE:{sequence}");
        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");

        return string.Join("\r\n", lines.SelectMany(Fold)) + "\r\n";
    }

    private static string BuildDescription(Interview interview)
    {
        var builder = new StringBuilder();
        builder.Append($"Vị trí: {interview.JobTitle}");
        builder.Append("\nThời lượng: ").Append(interview.DurationMinutes).Append(" phút");
        builder.Append("\nHình thức: ").Append(interview.Mode switch
        {
            InterviewMode.Online => "Trực tuyến",
            InterviewMode.Onsite => "Tại văn phòng",
            _ => "Điện thoại",
        });
        if (!string.IsNullOrWhiteSpace(interview.Location))
            builder.Append("\nĐịa điểm / liên kết: ").Append(interview.Location);
        if (!string.IsNullOrWhiteSpace(interview.Notes))
            builder.Append("\nGhi chú: ").Append(interview.Notes);

        return builder.ToString();
    }

    /// <summary>Escapes the four characters RFC 5545 reserves in text values, then keeps "\n" as a break.</summary>
    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", "\\n");
    }

    /// <summary>Folds one logical line so no physical line exceeds 75 UTF-8 octets.</summary>
    private static IEnumerable<string> Fold(string line)
    {
        var limit = 75;
        var current = new StringBuilder();
        var bytes = 0;

        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            // Continuation lines start with a space, which counts toward the limit.
            if (bytes + size > limit)
            {
                yield return current.ToString();
                current.Clear();
                current.Append(' ');
                bytes = 1;
            }

            current.Append(rune.ToString());
            bytes += size;
        }

        yield return current.ToString();
    }
}
