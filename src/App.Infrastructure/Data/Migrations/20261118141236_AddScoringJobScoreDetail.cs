using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScoringJobScoreDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScoreDetail",
                table: "scoring_jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScoreDetail",
                table: "scoring_jobs");
        }
    }
}
