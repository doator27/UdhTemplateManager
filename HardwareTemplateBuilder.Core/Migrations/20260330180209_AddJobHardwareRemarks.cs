using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJobHardwareRemarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Remarks",
                table: "JobHardware",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Remarks",
                table: "JobHardware");
        }
    }
}
