using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <summary>
    /// Ties job attachments to releases: adds <c>JobAttachments.ReleaseId</c>
    /// (existing attachments become the base list's).
    /// </summary>
    public partial class JobAttachmentPerRelease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReleaseId",
                table: "JobAttachments",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReleaseId",
                table: "JobAttachments");
        }
    }
}
