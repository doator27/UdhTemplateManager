using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class _20260321000000_AddJobLifecycleFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Jobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IsComplete",
                table: "Jobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "MissingTemplateNotifiedAt",
                table: "Jobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseId",
                table: "JobHardware",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BulkAddDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    DraftJson = table.Column<string>(type: "TEXT", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkAddDrafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BulkAddDrafts_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobReleases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseLabel = table.Column<string>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobReleases_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobHardware_ReleaseId",
                table: "JobHardware",
                column: "ReleaseId");

            migrationBuilder.CreateIndex(
                name: "IX_BulkAddDrafts_JobId",
                table: "BulkAddDrafts",
                column: "JobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobReleases_JobId",
                table: "JobReleases",
                column: "JobId");

            migrationBuilder.AddForeignKey(
                name: "FK_JobHardware_JobReleases_ReleaseId",
                table: "JobHardware",
                column: "ReleaseId",
                principalTable: "JobReleases",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_JobHardware_JobReleases_ReleaseId",
                table: "JobHardware");

            migrationBuilder.DropTable(
                name: "BulkAddDrafts");

            migrationBuilder.DropTable(
                name: "JobReleases");

            migrationBuilder.DropIndex(
                name: "IX_JobHardware_ReleaseId",
                table: "JobHardware");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "IsComplete",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "MissingTemplateNotifiedAt",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ReleaseId",
                table: "JobHardware");
        }
    }
}
