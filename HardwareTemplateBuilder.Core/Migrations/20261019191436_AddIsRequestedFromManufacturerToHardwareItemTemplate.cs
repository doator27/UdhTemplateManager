using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddIsRequestedFromManufacturerToHardwareItemTemplate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRequestedFromManufacturer",
                table: "HardwareItemTemplates",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsRequestedFromManufacturer",
                table: "HardwareItemTemplates");
        }
    }
}
