using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddIgnoredTemplateDuplicates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IgnoredTemplateDuplicates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SharedLink = table.Column<string>(type: "TEXT", nullable: false),
                    LinkType = table.Column<string>(type: "TEXT", nullable: false),
                    IgnoredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IgnoredTemplateDuplicates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IgnoredTemplateDuplicates_SharedLink_LinkType",
                table: "IgnoredTemplateDuplicates",
                columns: new[] { "SharedLink", "LinkType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IgnoredTemplateDuplicates");
        }
    }
}
