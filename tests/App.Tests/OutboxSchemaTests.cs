using App.Core.Entities;
using App.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace App.Tests;

/// <summary>
/// The publisher's raw claim SQL names the outbox table and columns literally. This test pins those names to
/// the EF model, so a rename cannot silently break the PostgreSQL claim query. No connection is opened.
/// </summary>
public class OutboxSchemaTests
{
    [Fact]
    public void OutboxTableAndColumnsMatchTheClaimSql()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=unused;Database=unused")
            .Options;
        using var db = new AppDbContext(options);

        var entity = db.Model.FindEntityType(typeof(OutboxEvent))!;

        Assert.Equal("outbox_events", entity.GetTableName());
        Assert.Equal("Id", entity.FindProperty(nameof(OutboxEvent.Id))!.GetColumnName());
        Assert.Equal("AggregateId", entity.FindProperty(nameof(OutboxEvent.AggregateId))!.GetColumnName());
        Assert.Equal("IsPublished", entity.FindProperty(nameof(OutboxEvent.IsPublished))!.GetColumnName());
        Assert.Equal("NextAttemptAt", entity.FindProperty(nameof(OutboxEvent.NextAttemptAt))!.GetColumnName());
        Assert.Equal("CreatedAt", entity.FindProperty(nameof(OutboxEvent.CreatedAt))!.GetColumnName());
    }
}
