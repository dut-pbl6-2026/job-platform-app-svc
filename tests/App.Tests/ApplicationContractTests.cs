using System.Text.Json;
using App.Core.DTOs;
using Xunit;

namespace App.Tests;

/// <summary>
/// Wire-shape contracts for the application API: which fields each audience receives, and the JSON
/// names clients send and read. Uses the ASP.NET Core default JSON options (web defaults, camelCase).
/// </summary>
public class ApplicationContractTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static IReadOnlyList<string> PropertyNames(Type type) =>
        type.GetProperties().Select(p => p.Name).ToList();

    private static HashSet<string> JsonKeys(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, WebOptions)).RootElement
            .EnumerateObject().Select(p => p.Name).ToHashSet();

    private static ApplicantApplicationDetailDto SampleApplicantView() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "cover", "/cv.pdf", "pending",
        DateTime.UtcNow, DateTime.UtcNow, [], ["reviewed", "rejected"]);

    private static RecruiterApplicationDetailDto SampleRecruiterView() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "cover", "/cv.pdf", "pending",
        RecruiterNotes: "internal", Score: 72.5, ScoreStatus: "done", DateTime.UtcNow, DateTime.UtcNow, [], ["reviewed"]);

    private static ApplicationSummaryDto SampleApplicantSummary() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "pending", "/cv.pdf", "cover", DateTime.UtcNow, DateTime.UtcNow);

    private static RecruiterApplicationSummaryDto SampleRecruiterSummary() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "pending", "/cv.pdf", "cover",
        Score: 61, ScoreStatus: "pending", DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public void ApplicantView_TypeHasNoRecruiterNotesProperty()
    {
        Assert.DoesNotContain("RecruiterNotes", PropertyNames(typeof(ApplicantApplicationDetailDto)));
    }

    [Fact]
    public void RecruiterView_TypeExposesRecruiterNotesAndScoreFields()
    {
        var names = PropertyNames(typeof(RecruiterApplicationDetailDto));
        Assert.Contains("RecruiterNotes", names);
        Assert.Contains("Score", names);
        Assert.Contains("ScoreStatus", names);
    }

    [Fact]
    public void ApplicantView_TypeHasNoScoreFields()
    {
        var names = PropertyNames(typeof(ApplicantApplicationDetailDto));
        Assert.DoesNotContain("Score", names);
        Assert.DoesNotContain("ScoreStatus", names);
    }

    [Fact]
    public void ApplicantView_HasExactlyTheRecruiterFieldsMinusNotesAndScoring()
    {
        var applicant = PropertyNames(typeof(ApplicantApplicationDetailDto));
        var recruiter = PropertyNames(typeof(RecruiterApplicationDetailDto));

        Assert.Equal(
            recruiter.Where(n => n is not ("RecruiterNotes" or "Score" or "ScoreStatus" or "ScoreDetail" or "ApplicantName" or "ApplicantEmail")),
            applicant);
    }

    [Fact]
    public void ApplicantJson_OmitsRecruiterNotesAndScoring_ButKeepsOtherFields()
    {
        var keys = JsonKeys(SampleApplicantView());

        Assert.DoesNotContain("recruiterNotes", keys);
        Assert.DoesNotContain("score", keys);
        Assert.DoesNotContain("scoreStatus", keys);
        Assert.True(keys.SetEquals(new[]
        {
            "id", "jobId", "applicantId", "coverLetter", "cvUrl", "status",
            "createdAt", "updatedAt", "statusHistory", "nextAllowedStatuses",
        }));
    }

    [Fact]
    public void ApplicantSummaryJson_HasNoScoreFields()
    {
        var keys = JsonKeys(SampleApplicantSummary());

        Assert.DoesNotContain("score", keys);
        Assert.DoesNotContain("scoreStatus", keys);
    }

    [Fact]
    public void RecruiterJson_IncludesRecruiterNotesScoreScoreStatusAndTheReport()
    {
        var keys = JsonKeys(SampleRecruiterView());

        Assert.Contains("recruiterNotes", keys);
        Assert.Contains("score", keys);
        Assert.Contains("scoreStatus", keys);
        Assert.Contains("scoreDetail", keys);
        Assert.Contains("applicantName", keys);
        Assert.Contains("applicantEmail", keys);
    }

    [Fact]
    public void RecruiterSummaryJson_IncludesScoreAndScoreStatus()
    {
        var keys = JsonKeys(SampleRecruiterSummary());

        Assert.Contains("score", keys);
        Assert.Contains("scoreStatus", keys);
        Assert.DoesNotContain("recruiterNotes", keys);
    }

    [Fact]
    public void UpdateStatusRequest_BindsCamelCaseClientPayload()
    {
        const string json = """{"status":"reviewed","note":"Good fit","recruiterNotes":"Call back"}""";

        var req = JsonSerializer.Deserialize<UpdateStatusRequest>(json, WebOptions);

        Assert.NotNull(req);
        Assert.Equal("reviewed", req.Status);
        Assert.Equal("Good fit", req.Note);
        Assert.Equal("Call back", req.RecruiterNotes);
    }

    [Fact]
    public void UpdateStatusRequest_OptionalFieldsDefaultToNull()
    {
        var req = JsonSerializer.Deserialize<UpdateStatusRequest>("""{"status":"rejected"}""", WebOptions);

        Assert.NotNull(req);
        Assert.Equal("rejected", req.Status);
        Assert.Null(req.Note);
        Assert.Null(req.RecruiterNotes);
    }

    [Fact]
    public void StatusFlowDto_UsesSnakeCaseKeysClientsRelyOn()
    {
        var dto = new StatusFlowDto(
            ["pending"], ["accepted"], new Dictionary<string, IReadOnlyList<string>> { ["pending"] = ["reviewed"] });

        Assert.True(JsonKeys(dto).SetEquals(new[] { "all_statuses", "terminal_statuses", "transitions" }));
    }

    [Fact]
    public void PaginatedResponse_UsesCamelCaseEnvelope()
    {
        var page = new PaginatedResponse<ApplicationSummaryDto>([], 0, 1, 20);

        Assert.True(JsonKeys(page).SetEquals(new[] { "items", "total", "page", "size" }));
    }
}
