using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJobLastActiveRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastActiveReleaseId",
                table: "Jobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_LastActiveReleaseId",
                table: "Jobs",
                column: "LastActiveReleaseId");

            migrationBuilder.AddForeignKey(
                name: "FK_Jobs_JobReleases_LastActiveReleaseId",
                table: "Jobs",
                column: "LastActiveReleaseId",
                principalTable: "JobReleases",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Jobs_JobReleases_LastActiveReleaseId",
                table: "Jobs");

            migrationBuilder.DropIndex(
                name: "IX_Jobs_LastActiveReleaseId",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "LastActiveReleaseId",
                table: "Jobs");
        }
    }
}
