using Xunit;
using App.Api.Services;
using App.Core.Entities;

namespace App.Tests;

public class EngagementEntityTests
{
    private static Interview NewInterview(
        DateTime? startsAt = null,
        int duration = 45,
        string? applicantEmail = "candidate@example.test")
    {
        return new Interview(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Lập trình viên Backend",
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Nguyễn Văn An",
            applicantEmail!,
            Guid.NewGuid(),
            "Nhà tuyển dụng",
            "recruiter@example.test",
            startsAt ?? DateTime.UtcNow.AddDays(2),
            duration,
            InterviewMode.Online,
            "https://meet.example.test/room",
            "Mang theo portfolio");
    }

    [Fact]
    public void Interview_starts_scheduled_with_a_stable_calendar_uid_and_end()
    {
        var start = new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc);
        var interview = NewInterview(start);

        Assert.Equal(InterviewStatus.Scheduled, interview.Status);
        Assert.Equal(start, interview.StartsAtUtc);
        Assert.Equal(start.AddMinutes(45), interview.EndsAtUtc);
        Assert.Equal(interview.Id.ToString(), interview.CalendarUid);
    }

    [Fact]
    public void Interview_requires_the_candidate_email()
    {
        var error = Assert.Throws<ArgumentException>(() => NewInterview(applicantEmail: " "));
        Assert.Contains("email", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(500)]
    public void Interview_duration_must_be_within_bounds(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewInterview(duration: minutes));
    }

    [Fact]
    public void Cancelling_twice_is_refused()
    {
        var interview = NewInterview();
        interview.Cancel();

        Assert.Equal(InterviewStatus.Cancelled, interview.Status);
        Assert.Throws<InvalidOperationException>(() => interview.Cancel());
    }

    [Fact]
    public void Email_template_trims_and_rejects_blank_content()
    {
        var template = new EmailTemplate(Guid.NewGuid(), "  Mời phỏng vấn ", " Tiêu đề ", " Nội dung ");
        Assert.Equal("Mời phỏng vấn", template.Name);
        Assert.Equal("Tiêu đề", template.Subject);

        Assert.Throws<ArgumentException>(() => new EmailTemplate(Guid.NewGuid(), " ", "Tiêu đề", "Nội dung"));
    }

    [Fact]
    public void Calendar_invite_is_a_request_with_escaped_text_and_folded_lines()
    {
        var start = new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc);
        var interview = NewInterview(start);
        var ics = IcsBuilder.BuildInvite(interview);

        Assert.Contains("METHOD:REQUEST", ics);
        Assert.Contains("STATUS:CONFIRMED", ics);
        Assert.Contains("DTSTART:20261201T090000Z", ics);
        Assert.Contains("DTEND:20261201T094500Z", ics);
        Assert.Contains($"UID:{interview.CalendarUid}", ics);
        // Folding inserts CRLF + space inside a long line; unfold before matching values.
        var unfolded = ics.Replace("\r\n ", "");
        Assert.Contains("mailto:candidate@example.test", unfolded);
        Assert.Contains("mailto:recruiter@example.test", unfolded);

        foreach (var line in ics.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(line) <= 75,
                $"line exceeds 75 octets: {line}");
        }
    }

    [Fact]
    public void Calendar_cancellation_reuses_the_uid_and_method()
    {
        var interview = NewInterview();
        interview.Cancel();
        var ics = IcsBuilder.BuildCancellation(interview);

        Assert.Contains("METHOD:CANCEL", ics);
        Assert.Contains("STATUS:CANCELLED", ics);
        Assert.Contains($"UID:{interview.CalendarUid}", ics);
        Assert.Contains("SEQUENCE:1", ics);
    }
}
