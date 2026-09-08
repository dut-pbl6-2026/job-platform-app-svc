using App.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<StatusHistory> StatusHistories => Set<StatusHistory>();

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
    }
}
