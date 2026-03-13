using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class WeightToDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PRAGMA foreign_keys cannot be changed inside a transaction (SQLite ignores it).
            // suppressTransaction: true executes this command on the raw connection before
            // EF Core opens its migration transaction, so the setting actually takes effect.
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualTemplates_Weights_WeightId",
                table: "IndividualTemplates");

            migrationBuilder.DropTable(
                name: "Weights");

            migrationBuilder.DropIndex(
                name: "IX_IndividualTemplates_WeightId",
                table: "IndividualTemplates");

            migrationBuilder.DropColumn(
                name: "WeightId",
                table: "IndividualTemplates");

            migrationBuilder.AddColumn<string>(
                name: "WeightValue",
                table: "Descriptions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WeightValue",
                table: "Descriptions");

            migrationBuilder.AddColumn<int>(
                name: "WeightId",
                table: "IndividualTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Weights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DescriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    WeightValue = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Weights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Weights_Descriptions_DescriptionId",
                        column: x => x.DescriptionId,
                        principalTable: "Descriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualTemplates_WeightId",
                table: "IndividualTemplates",
                column: "WeightId");

            migrationBuilder.CreateIndex(
                name: "IX_Weights_DescriptionId",
                table: "Weights",
                column: "DescriptionId");

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualTemplates_Weights_WeightId",
                table: "IndividualTemplates",
                column: "WeightId",
                principalTable: "Weights",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
