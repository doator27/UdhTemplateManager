using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJobScopedTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OriginJobId",
                table: "IndividualTemplates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "JobId",
                table: "HardwareItemTemplates",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndividualTemplates_OriginJobId",
                table: "IndividualTemplates",
                column: "OriginJobId");

            migrationBuilder.CreateIndex(
                name: "IX_HardwareItemTemplates_JobId",
                table: "HardwareItemTemplates",
                column: "JobId");

            migrationBuilder.AddForeignKey(
                name: "FK_HardwareItemTemplates_Jobs_JobId",
                table: "HardwareItemTemplates",
                column: "JobId",
                principalTable: "Jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualTemplates_Jobs_OriginJobId",
                table: "IndividualTemplates",
                column: "OriginJobId",
                principalTable: "Jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HardwareItemTemplates_Jobs_JobId",
                table: "HardwareItemTemplates");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualTemplates_Jobs_OriginJobId",
                table: "IndividualTemplates");

            migrationBuilder.DropIndex(
                name: "IX_IndividualTemplates_OriginJobId",
                table: "IndividualTemplates");

            migrationBuilder.DropIndex(
                name: "IX_HardwareItemTemplates_JobId",
                table: "HardwareItemTemplates");

            migrationBuilder.DropColumn(
                name: "OriginJobId",
                table: "IndividualTemplates");

            migrationBuilder.DropColumn(
                name: "JobId",
                table: "HardwareItemTemplates");
        }
    }
}
