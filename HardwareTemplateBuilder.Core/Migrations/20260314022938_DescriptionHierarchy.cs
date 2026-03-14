using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class DescriptionHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "Category",
                table: "Descriptions");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);

            migrationBuilder.AddColumn<int>(
                name: "ParentId",
                table: "Descriptions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Descriptions_ParentId",
                table: "Descriptions",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_Descriptions_Descriptions_ParentId",
                table: "Descriptions",
                column: "ParentId",
                principalTable: "Descriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Descriptions_Descriptions_ParentId",
                table: "Descriptions");

            migrationBuilder.DropIndex(
                name: "IX_Descriptions_ParentId",
                table: "Descriptions");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Descriptions");

            migrationBuilder.AddColumn<int>(
                name: "Category",
                table: "Descriptions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
