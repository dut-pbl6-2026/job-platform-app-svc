using App.Core.Entities;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace App.Tests;

/// <summary>
/// The worker's claim SQL names the table and columns directly. These tests fail if the model is renamed
/// without updating <c>ResumeScoringWorkerService.ClaimRelationalAsync</c>.
/// </summary>
public class ScoringSchemaTests
{
    private static AppDbContext NewModelContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=unused;Database=unused")
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public void ScoringTableAndColumnsMatchTheClaimSql()
    {
        using var db = NewModelContext();
        var entity = db.Model.FindEntityType(typeof(ScoringJob))!;

        Assert.Equal("scoring_jobs", entity.GetTableName());
        Assert.Equal("Status", entity.FindProperty(nameof(ScoringJob.Status))!.GetColumnName());
        Assert.Equal("NextAttemptAt", entity.FindProperty(nameof(ScoringJob.NextAttemptAt))!.GetColumnName());
        Assert.Equal("Id", entity.FindProperty(nameof(ScoringJob.Id))!.GetColumnName());
        Assert.Equal("CreatedAt", entity.FindProperty(nameof(ScoringJob.CreatedAt))!.GetColumnName());
    }

    [Fact]
    public void StatusIsStoredAsTheEnumName()
    {
        using var db = NewModelContext();
        var status = db.Model.FindEntityType(typeof(ScoringJob))!.FindProperty(nameof(ScoringJob.Status))!;

        // The claim SQL compares "Status" with the enum name, so the provider value must be a string.
        Assert.Equal(typeof(string), status.GetProviderClrType());
    }

    [Fact]
    public void OneScoringJobPerApplication()
    {
        using var db = NewModelContext();
        var entity = db.Model.FindEntityType(typeof(ScoringJob))!;

        var index = Assert.Single(entity.GetIndexes(), i => i.IsUnique);
        Assert.Equal("IX_scoring_jobs_application_id", index.GetDatabaseName());
        Assert.Equal(nameof(ScoringJob.ApplicationId), index.Properties.Single().Name);
    }
}
