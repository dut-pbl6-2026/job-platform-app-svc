using App.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<StatusHistory> StatusHistories => Set<StatusHistory>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Application>(e =>
        {
            e.ToTable("applications");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.JobId).IsRequired();
            e.Property(x => x.ApplicantId).IsRequired();
            e.Property(x => x.CvUrl).HasMaxLength(Application.CvUrlMaxLength).IsRequired();
            e.Property(x => x.CoverLetter).HasMaxLength(Application.CoverLetterMaxLength);
            e.Property(x => x.RecruiterNotes).HasMaxLength(Application.RecruiterNotesMaxLength);
            e.Property(x => x.Score);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();

            // Optimistic concurrency: an UPDATE is conditioned on the RowVersion that was loaded, so a
            // concurrent status change fails with DbUpdateConcurrencyException instead of overwriting it.
            e.Property(x => x.RowVersion).IsRequired().IsConcurrencyToken();

            e.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(ApplicationStatus.Pending);

            // APP-01-02: Prevent duplicate applications per user per job
            e.HasIndex(x => new { x.JobId, x.ApplicantId })
                .IsUnique()
                .HasDatabaseName("IX_applications_job_applicant_unique");

            e.HasIndex(x => x.JobId).HasDatabaseName("IX_applications_job_id");
            e.HasIndex(x => x.ApplicantId).HasDatabaseName("IX_applications_applicant_id");
            e.HasIndex(x => x.Status).HasDatabaseName("IX_applications_status");

            e.HasMany(x => x.StatusHistories)
                .WithOne(x => x.Application)
                .HasForeignKey(x => x.ApplicationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StatusHistory>(e =>
        {
            e.ToTable("status_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.ApplicationId).IsRequired();
            e.Property(x => x.ChangedBy).IsRequired();
            e.Property(x => x.ChangedAt).IsRequired();
            e.Property(x => x.Note).HasMaxLength(StatusHistory.NoteMaxLength);

            e.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(32);

            e.HasIndex(x => x.ApplicationId).HasDatabaseName("IX_status_history_application_id");
        });

        b.Entity<OutboxEvent>(e =>
        {
            e.ToTable("outbox_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            e.Property(x => x.EventType).HasMaxLength(OutboxEvent.EventTypeMaxLength).IsRequired();
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.AggregateId).IsRequired();
            e.Property(x => x.IsPublished).HasDefaultValue(false);
            e.Property(x => x.CreatedAt).IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
            e.Property(x => x.NextAttemptAt).IsRequired();

            // Publisher poll: unpublished rows in publish order.
            e.HasIndex(x => new { x.IsPublished, x.CreatedAt })
                .HasDatabaseName("IX_outbox_events_is_published_created_at");

            // Per-application ordering check: earlier unpublished rows of the same application.
            e.HasIndex(x => new { x.AggregateId, x.CreatedAt })
                .HasFilter("NOT \"IsPublished\"")
                .HasDatabaseName("IX_outbox_events_unpublished_aggregate");
        });
    }
}
