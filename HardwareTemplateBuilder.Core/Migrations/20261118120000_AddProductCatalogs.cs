using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <summary>Adds the product catalog tables (catalog PDFs and the text of each page).</summary>
    public partial class AddProductCatalogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductCatalogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ManufacturerId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    OnlineLink = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LocalLink = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IndexedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCatalogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductCatalogPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CatalogId = table.Column<int>(type: "int", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SearchKey = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductCatalogPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductCatalogPages_ProductCatalogs_CatalogId",
                        column: x => x.CatalogId,
                        principalTable: "ProductCatalogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductCatalogs_ManufacturerId",
                table: "ProductCatalogs",
                column: "ManufacturerId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductCatalogPages_CatalogId_PageNumber",
                table: "ProductCatalogPages",
                columns: new[] { "CatalogId", "PageNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ProductCatalogPages");
            migrationBuilder.DropTable(name: "ProductCatalogs");
        }
    }
}
