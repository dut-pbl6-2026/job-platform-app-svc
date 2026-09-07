using App.Core.Entities;
using Xunit;

namespace App.Tests;

public class ApplicationEntityTests
{
    [Fact]
    public void Constructor_WithValidData_InitializesCorrectlyAndAddsPendingHistory()
    {
        var jobId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();
        var cvUrl = "/api/applications/cv/resume.pdf";
        var coverLetter = "Excited about this role.";

        var app = new Application(jobId, applicantId, cvUrl, coverLetter);

        Assert.NotEqual(Guid.Empty, app.Id);
        Assert.Equal(jobId, app.JobId);
        Assert.Equal(applicantId, app.ApplicantId);
        Assert.Equal(cvUrl, app.CvUrl);
        Assert.Equal(coverLetter, app.CoverLetter);
        Assert.Equal(ApplicationStatus.Pending, app.Status);
        Assert.Null(app.RecruiterNotes);
        Assert.Null(app.Score);

        // Status history must have initial pending record
        Assert.Single(app.StatusHistories);
        var initialHistory = app.StatusHistories.First();
        Assert.Equal(ApplicationStatus.Pending, initialHistory.Status);
        Assert.Equal(applicantId, initialHistory.ChangedBy);
        Assert.Equal("Application submitted", initialHistory.Note);
    }

    [Fact]
    public void Constructor_WithEmptyJobId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new Application(Guid.Empty, Guid.NewGuid(), "/cv.pdf"));
    }

    [Fact]
    public void Constructor_WithEmptyApplicantId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new Application(Guid.NewGuid(), Guid.Empty, "/cv.pdf"));
    }

    [Fact]
    public void Constructor_WithEmptyCvUrl_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            new Application(Guid.NewGuid(), Guid.NewGuid(), "   "));
    }

    [Fact]
    public void UpdateStatus_ValidStatus_UpdatesStatusAndAppendsHistory()
    {
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");
        var recruiterId = Guid.NewGuid();

        app.UpdateStatus(ApplicationStatus.Reviewed, recruiterId, "Candidate profile looks good.");

        Assert.Equal(ApplicationStatus.Reviewed, app.Status);
        Assert.Equal(2, app.StatusHistories.Count);

        var latestHistory = app.StatusHistories.Last();
        Assert.Equal(ApplicationStatus.Reviewed, latestHistory.Status);
        Assert.Equal(recruiterId, latestHistory.ChangedBy);
        Assert.Equal("Candidate profile looks good.", latestHistory.Note);
    }

    [Fact]
    public void SetRecruiterNotes_And_SetScore_UpdatesProperties()
    {
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");

        app.SetRecruiterNotes("Top candidate for interview round 2");
        app.SetScore(85.5);

        Assert.Equal("Top candidate for interview round 2", app.RecruiterNotes);
        Assert.Equal(85.5, app.Score);
    }
}
