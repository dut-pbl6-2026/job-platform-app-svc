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
        Score: 72.5, DateTime.UtcNow, DateTime.UtcNow, [], ["reviewed", "rejected"]);

    private static RecruiterApplicationDetailDto SampleRecruiterView() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "cover", "/cv.pdf", "pending",
        RecruiterNotes: "internal", Score: 72.5, DateTime.UtcNow, DateTime.UtcNow, [], ["reviewed"]);

    [Fact]
    public void ApplicantView_TypeHasNoRecruiterNotesProperty()
    {
        Assert.DoesNotContain("RecruiterNotes", PropertyNames(typeof(ApplicantApplicationDetailDto)));
    }

    [Fact]
    public void RecruiterView_TypeExposesRecruiterNotesAndScore()
    {
        var names = PropertyNames(typeof(RecruiterApplicationDetailDto));
        Assert.Contains("RecruiterNotes", names);
        Assert.Contains("Score", names);
    }

    [Fact]
    public void ApplicantView_HasExactlyTheRecruiterFieldsMinusRecruiterNotes()
    {
        var applicant = PropertyNames(typeof(ApplicantApplicationDetailDto));
        var recruiter = PropertyNames(typeof(RecruiterApplicationDetailDto));

        Assert.Equal(recruiter.Where(n => n != "RecruiterNotes"), applicant);
    }

    [Fact]
    public void ApplicantJson_OmitsRecruiterNotes_ButKeepsOtherFields()
    {
        var keys = JsonKeys(SampleApplicantView());

        Assert.DoesNotContain("recruiterNotes", keys);
        Assert.True(keys.SetEquals(new[]
        {
            "id", "jobId", "applicantId", "coverLetter", "cvUrl", "status", "score",
            "createdAt", "updatedAt", "statusHistory", "nextAllowedStatuses",
        }));
    }

    [Fact]
    public void RecruiterJson_IncludesRecruiterNotes()
    {
        var keys = JsonKeys(SampleRecruiterView());

        Assert.Contains("recruiterNotes", keys);
        Assert.Contains("score", keys);
    }

    [Fact]
    public void UpdateStatusRequest_BindsCamelCaseClientPayload()
    {
        const string json = """{"status":"reviewed","note":"Good fit","recruiterNotes":"Call back","score":88.0}""";

        var req = JsonSerializer.Deserialize<UpdateStatusRequest>(json, WebOptions);

        Assert.NotNull(req);
        Assert.Equal("reviewed", req.Status);
        Assert.Equal("Good fit", req.Note);
        Assert.Equal("Call back", req.RecruiterNotes);
        Assert.Equal(88.0, req.Score);
    }

    [Fact]
    public void UpdateStatusRequest_OptionalFieldsDefaultToNull()
    {
        var req = JsonSerializer.Deserialize<UpdateStatusRequest>("""{"status":"rejected"}""", WebOptions);

        Assert.NotNull(req);
        Assert.Equal("rejected", req.Status);
        Assert.Null(req.Note);
        Assert.Null(req.RecruiterNotes);
        Assert.Null(req.Score);
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
