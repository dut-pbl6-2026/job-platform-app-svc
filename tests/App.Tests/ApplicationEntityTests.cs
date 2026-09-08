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

    [Fact]
    public void StatusFlow_FullPipeline_SucceedsWithCompleteAuditTrail()
    {
        var applicantId = Guid.NewGuid();
        var recruiterId = Guid.NewGuid();
        var app = new Application(Guid.NewGuid(), applicantId, "/cv.pdf");

        // Step 1: Pending -> Reviewed
        Assert.True(app.CanTransitionTo(ApplicationStatus.Reviewed));
        app.UpdateStatus(ApplicationStatus.Reviewed, recruiterId, "CV screened");
        Assert.Equal(ApplicationStatus.Reviewed, app.Status);

        // Step 2: Reviewed -> Shortlisted
        Assert.True(app.CanTransitionTo(ApplicationStatus.Shortlisted));
        app.UpdateStatus(ApplicationStatus.Shortlisted, recruiterId, "Interview scheduled");
        Assert.Equal(ApplicationStatus.Shortlisted, app.Status);

        // Step 3: Shortlisted -> Accepted
        Assert.True(app.CanTransitionTo(ApplicationStatus.Accepted));
        app.UpdateStatus(ApplicationStatus.Accepted, recruiterId, "Offer accepted");
        Assert.Equal(ApplicationStatus.Accepted, app.Status);

        // Verify full history has 4 records
        Assert.Equal(4, app.StatusHistories.Count);
        var statuses = app.StatusHistories.Select(h => h.Status).ToList();
        Assert.Equal(new[] { ApplicationStatus.Pending, ApplicationStatus.Reviewed, ApplicationStatus.Shortlisted, ApplicationStatus.Accepted }, statuses);
    }

    [Theory]
    [InlineData(ApplicationStatus.Pending)]
    [InlineData(ApplicationStatus.Reviewed)]
    [InlineData(ApplicationStatus.Shortlisted)]
    public void StatusFlow_CanRejectFromNonTerminalStages(ApplicationStatus stage)
    {
        var recruiterId = Guid.NewGuid();
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");

        if (stage == ApplicationStatus.Reviewed)
        {
            app.UpdateStatus(ApplicationStatus.Reviewed, recruiterId);
        }
        else if (stage == ApplicationStatus.Shortlisted)
        {
            app.UpdateStatus(ApplicationStatus.Reviewed, recruiterId);
            app.UpdateStatus(ApplicationStatus.Shortlisted, recruiterId);
        }

        Assert.True(app.CanTransitionTo(ApplicationStatus.Rejected));
        app.UpdateStatus(ApplicationStatus.Rejected, recruiterId, "Does not meet requirements");
        Assert.Equal(ApplicationStatus.Rejected, app.Status);
    }

    [Fact]
    public void StatusFlow_TransitionToSameStatus_ThrowsInvalidOperationException()
    {
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");
        var recruiterId = Guid.NewGuid();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            app.UpdateStatus(ApplicationStatus.Pending, recruiterId));
        Assert.Contains("already in 'Pending' status", ex.Message);
    }

    [Fact]
    public void StatusFlow_InvalidTransition_ThrowsInvalidOperationException()
    {
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");
        var recruiterId = Guid.NewGuid();

        // Cannot skip directly to Accepted from Pending
        Assert.False(app.CanTransitionTo(ApplicationStatus.Accepted));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            app.UpdateStatus(ApplicationStatus.Accepted, recruiterId));
        Assert.Contains("Invalid status transition", ex.Message);
    }

    [Fact]
    public void StatusFlow_TerminalStates_CannotTransitionFurther()
    {
        var recruiterId = Guid.NewGuid();

        // Accepted is terminal
        var acceptedApp = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");
        acceptedApp.UpdateStatus(ApplicationStatus.Reviewed, recruiterId);
        acceptedApp.UpdateStatus(ApplicationStatus.Shortlisted, recruiterId);
        acceptedApp.UpdateStatus(ApplicationStatus.Accepted, recruiterId);
        Assert.Empty(acceptedApp.GetAllowedTransitions());
        Assert.False(acceptedApp.CanTransitionTo(ApplicationStatus.Rejected));

        Assert.Throws<InvalidOperationException>(() =>
            acceptedApp.UpdateStatus(ApplicationStatus.Rejected, recruiterId));

        // Rejected is terminal
        var rejectedApp = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");
        rejectedApp.UpdateStatus(ApplicationStatus.Rejected, recruiterId);
        Assert.Empty(rejectedApp.GetAllowedTransitions());
        Assert.False(rejectedApp.CanTransitionTo(ApplicationStatus.Reviewed));

        Assert.Throws<InvalidOperationException>(() =>
            rejectedApp.UpdateStatus(ApplicationStatus.Reviewed, recruiterId));
    }

    [Fact]
    public void StatusFlow_PendingCannotSkipToShortlisted()
    {
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");
        var recruiterId = Guid.NewGuid();

        Assert.False(app.CanTransitionTo(ApplicationStatus.Shortlisted));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            app.UpdateStatus(ApplicationStatus.Shortlisted, recruiterId));
        Assert.Contains("Invalid status transition", ex.Message);
    }

    [Fact]
    public void SetRecruiterNotes_ExceedingMaxLength_ThrowsArgumentException()
    {
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");
        var longNotes = new string('A', Application.RecruiterNotesMaxLength + 1);

        var ex = Assert.Throws<ArgumentException>(() => app.SetRecruiterNotes(longNotes));
        Assert.Contains("Recruiter notes cannot exceed", ex.Message);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(-10.0)]
    [InlineData(100.1)]
    [InlineData(150.0)]
    public void SetScore_OutOfRange_ThrowsArgumentOutOfRangeException(double invalidScore)
    {
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");

        Assert.Throws<ArgumentOutOfRangeException>(() => app.SetScore(invalidScore));
    }

    [Fact]
    public void SetScore_ValidRange_Succeeds()
    {
        var app = new Application(Guid.NewGuid(), Guid.NewGuid(), "/cv.pdf");

        app.SetScore(0.0);
        Assert.Equal(0.0, app.Score);

        app.SetScore(100.0);
        Assert.Equal(100.0, app.Score);

        app.SetScore(85.5);
        Assert.Equal(85.5, app.Score);

        app.SetScore(null);
        Assert.Null(app.Score);
    }
}
