using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class DescriptionSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "WeightValue",
                table: "Descriptions");

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Descriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Descriptions");

            migrationBuilder.AddColumn<string>(
                name: "WeightValue",
                table: "Descriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
