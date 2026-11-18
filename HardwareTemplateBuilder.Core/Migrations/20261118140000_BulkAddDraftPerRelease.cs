using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <summary>
    /// Gives each job release its own bulk add draft: adds <c>BulkAddDrafts.ReleaseId</c>
    /// (existing drafts become the base list's) and replaces the one-per-job unique index.
    /// </summary>
    public partial class BulkAddDraftPerRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BulkAddDrafts_JobId",
                table: "BulkAddDrafts");

            migrationBuilder.AddColumn<int>(
                name: "ReleaseId",
                table: "BulkAddDrafts",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BulkAddDrafts_JobId_ReleaseId",
                table: "BulkAddDrafts",
                columns: new[] { "JobId", "ReleaseId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BulkAddDrafts_JobId_ReleaseId",
                table: "BulkAddDrafts");

            migrationBuilder.DropColumn(
                name: "ReleaseId",
                table: "BulkAddDrafts");

            migrationBuilder.CreateIndex(
                name: "IX_BulkAddDrafts_JobId",
                table: "BulkAddDrafts",
                column: "JobId",
                unique: true);
        }
    }
}
