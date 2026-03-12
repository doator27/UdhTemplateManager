using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CustomerName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Descriptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DescriptionText = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Descriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DoorMaterials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Material = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoorMaterials", x => x.Id);
                    table.CheckConstraint("CK_DoorMaterial_Material", "\"Material\" IN ('Hollow Metal', 'Wood')");
                });

            migrationBuilder.CreateTable(
                name: "Manufacturers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ManufacturerName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Manufacturers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectManagers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProjectManagerName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectManagers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    DefaultTemplateSaveLocation = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Weights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeightValue = table.Column<string>(type: "TEXT", nullable: false),
                    DescriptionId = table.Column<int>(type: "INTEGER", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "HardwareItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ManufacturerId = table.Column<int>(type: "INTEGER", nullable: false),
                    DescriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelNumber = table.Column<string>(type: "TEXT", nullable: false),
                    Remarks = table.Column<string>(type: "TEXT", nullable: true),
                    Frequency = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardwareItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HardwareItems_Descriptions_DescriptionId",
                        column: x => x.DescriptionId,
                        principalTable: "Descriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HardwareItems_Manufacturers_ManufacturerId",
                        column: x => x.ManufacturerId,
                        principalTable: "Manufacturers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobNumber = table.Column<string>(type: "TEXT", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectManagerId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserProfileId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Jobs_ProjectManagers_ProjectManagerId",
                        column: x => x.ProjectManagerId,
                        principalTable: "ProjectManagers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Jobs_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IndividualTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ManufacturerId = table.Column<int>(type: "INTEGER", nullable: false),
                    DescriptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateNumber = table.Column<string>(type: "TEXT", nullable: false),
                    NumPages = table.Column<int>(type: "INTEGER", nullable: false),
                    PagesToPrint = table.Column<string>(type: "TEXT", nullable: false),
                    PagesToRotate = table.Column<string>(type: "TEXT", nullable: true),
                    RotationDirection = table.Column<int>(type: "INTEGER", nullable: false),
                    WeightId = table.Column<int>(type: "INTEGER", nullable: false),
                    DoorMaterialId = table.Column<int>(type: "INTEGER", nullable: false),
                    OnlineLink = table.Column<string>(type: "TEXT", nullable: true),
                    LocalLink = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualTemplates_Descriptions_DescriptionId",
                        column: x => x.DescriptionId,
                        principalTable: "Descriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndividualTemplates_DoorMaterials_DoorMaterialId",
                        column: x => x.DoorMaterialId,
                        principalTable: "DoorMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndividualTemplates_Manufacturers_ManufacturerId",
                        column: x => x.ManufacturerId,
                        principalTable: "Manufacturers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IndividualTemplates_Weights_WeightId",
                        column: x => x.WeightId,
                        principalTable: "Weights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JobHardware",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    HardwareItemId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobHardware", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobHardware_HardwareItems_HardwareItemId",
                        column: x => x.HardwareItemId,
                        principalTable: "HardwareItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobHardware_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HardwareItemTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HardwareItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    IndividualTemplateId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HardwareItemTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HardwareItemTemplates_HardwareItems_HardwareItemId",
                        column: x => x.HardwareItemId,
                        principalTable: "HardwareItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HardwareItemTemplates_IndividualTemplates_IndividualTemplateId",
                        column: x => x.IndividualTemplateId,
                        principalTable: "IndividualTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JobTemplateSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    IndividualTemplateId = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotLocalLink = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotDate = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "datetime('now')"),
                    PagesToPrint = table.Column<string>(type: "TEXT", nullable: false),
                    PagesToRotate = table.Column<string>(type: "TEXT", nullable: true),
                    RotationDirection = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTemplateSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JobTemplateSnapshots_IndividualTemplates_IndividualTemplateId",
                        column: x => x.IndividualTemplateId,
                        principalTable: "IndividualTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JobTemplateSnapshots_Jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "Jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "DoorMaterials",
                columns: new[] { "Id", "Material" },
                values: new object[,]
                {
                    { 1, "Hollow Metal" },
                    { 2, "Wood" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_HardwareItems_DescriptionId",
                table: "HardwareItems",
                column: "DescriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_HardwareItems_ManufacturerId",
                table: "HardwareItems",
                column: "ManufacturerId");

            migrationBuilder.CreateIndex(
                name: "IX_HardwareItemTemplates_HardwareItemId",
                table: "HardwareItemTemplates",
                column: "HardwareItemId");

            migrationBuilder.CreateIndex(
                name: "IX_HardwareItemTemplates_IndividualTemplateId",
                table: "HardwareItemTemplates",
                column: "IndividualTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualTemplates_DescriptionId",
                table: "IndividualTemplates",
                column: "DescriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualTemplates_DoorMaterialId",
                table: "IndividualTemplates",
                column: "DoorMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualTemplates_ManufacturerId",
                table: "IndividualTemplates",
                column: "ManufacturerId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualTemplates_WeightId",
                table: "IndividualTemplates",
                column: "WeightId");

            migrationBuilder.CreateIndex(
                name: "IX_JobHardware_HardwareItemId",
                table: "JobHardware",
                column: "HardwareItemId");

            migrationBuilder.CreateIndex(
                name: "IX_JobHardware_JobId",
                table: "JobHardware",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_CustomerId",
                table: "Jobs",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ProjectManagerId",
                table: "Jobs",
                column: "ProjectManagerId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_UserProfileId",
                table: "Jobs",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplateSnapshots_IndividualTemplateId",
                table: "JobTemplateSnapshots",
                column: "IndividualTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_JobTemplateSnapshots_JobId",
                table: "JobTemplateSnapshots",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Weights_DescriptionId",
                table: "Weights",
                column: "DescriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HardwareItemTemplates");

            migrationBuilder.DropTable(
                name: "JobHardware");

            migrationBuilder.DropTable(
                name: "JobTemplateSnapshots");

            migrationBuilder.DropTable(
                name: "HardwareItems");

            migrationBuilder.DropTable(
                name: "IndividualTemplates");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "DoorMaterials");

            migrationBuilder.DropTable(
                name: "Manufacturers");

            migrationBuilder.DropTable(
                name: "Weights");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropTable(
                name: "ProjectManagers");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "Descriptions");
        }
    }
}
