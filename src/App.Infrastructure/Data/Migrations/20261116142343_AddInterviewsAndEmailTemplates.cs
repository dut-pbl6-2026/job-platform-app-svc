using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInterviewsAndEmailTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApplicantEmail",
                table: "applications",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApplicantName",
                table: "applications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "email_templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecruiterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_templates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "interviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CompanyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApplicantEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    RecruiterId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecruiterName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecruiterEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    StartsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CalendarUid = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_templates_recruiter_updated",
                table: "email_templates",
                columns: new[] { "RecruiterId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_interviews_applicant_id",
                table: "interviews",
                column: "ApplicantId");

            migrationBuilder.CreateIndex(
                name: "IX_interviews_application_id",
                table: "interviews",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_interviews_recruiter_id",
                table: "interviews",
                column: "RecruiterId");

            migrationBuilder.CreateIndex(
                name: "IX_interviews_starts_at",
                table: "interviews",
                column: "StartsAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_templates");

            migrationBuilder.DropTable(
                name: "interviews");

            migrationBuilder.DropColumn(
                name: "ApplicantEmail",
                table: "applications");

            migrationBuilder.DropColumn(
                name: "ApplicantName",
                table: "applications");
        }
    }
}
