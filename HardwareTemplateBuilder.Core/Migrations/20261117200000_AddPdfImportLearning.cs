using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <summary>Adds the tables where "Parse PDF into Bulk Add" stores what it has learned.</summary>
    public partial class AddPdfImportLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PdfImportItemMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ManufacturerCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SourceKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    HardwareItemId = table.Column<int>(type: "int", nullable: true),
                    TargetManufacturerId = table.Column<int>(type: "int", nullable: true),
                    TargetModelNumber = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Label = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsSkip = table.Column<bool>(type: "bit", nullable: false),
                    ConfirmCount = table.Column<int>(type: "int", nullable: false),
                    LastConfirmedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PdfImportItemMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PdfImportManufacturerAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ManufacturerId = table.Column<int>(type: "int", nullable: false),
                    ConfirmCount = table.Column<int>(type: "int", nullable: false),
                    LastConfirmedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PdfImportManufacturerAliases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PdfImportItemMappings_ManufacturerCode_SourceKey",
                table: "PdfImportItemMappings",
                columns: new[] { "ManufacturerCode", "SourceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PdfImportManufacturerAliases_Code_ManufacturerId",
                table: "PdfImportManufacturerAliases",
                columns: new[] { "Code", "ManufacturerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PdfImportItemMappings");
            migrationBuilder.DropTable(name: "PdfImportManufacturerAliases");
        }
    }
}
