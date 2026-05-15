using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddIsActiveToHardwareItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "HardwareItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            // Explicitly set all existing hardware items to active (belt-and-suspenders approach)
            migrationBuilder.Sql("UPDATE HardwareItems SET IsActive = 1 WHERE IsActive IS NULL OR IsActive = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "HardwareItems");
        }
    }
}
