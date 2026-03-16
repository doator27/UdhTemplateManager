using HardwareTemplateBuilder.Core.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HardwareTemplateBuilder.Core.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20260315000001_DoorMaterialValues")]
    public partial class DoorMaterialValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            // SQLite does not support ALTER TABLE for check constraints; recreate the table.
            migrationBuilder.Sql(@"
                CREATE TABLE ""DoorMaterials_new"" (
                    ""Id""       INTEGER NOT NULL CONSTRAINT ""PK_DoorMaterials"" PRIMARY KEY AUTOINCREMENT,
                    ""Material"" TEXT    NOT NULL,
                    CONSTRAINT ""CK_DoorMaterial_Material"" CHECK (""Material"" IN ('Metal', 'Wood', 'Both'))
                );

                INSERT INTO ""DoorMaterials_new"" (""Id"", ""Material"")
                    SELECT ""Id"",
                           CASE WHEN ""Material"" = 'Hollow Metal' THEN 'Metal' ELSE ""Material"" END
                    FROM ""DoorMaterials"";

                DROP TABLE ""DoorMaterials"";
                ALTER TABLE ""DoorMaterials_new"" RENAME TO ""DoorMaterials"";
            ");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);

            // Insert the new 'Both' seed record (Id=3) — raw SQL avoids needing a model snapshot.
            migrationBuilder.Sql("INSERT OR IGNORE INTO \"DoorMaterials\" (\"Id\", \"Material\") VALUES (3, 'Both');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            migrationBuilder.Sql(@"
                CREATE TABLE ""DoorMaterials_new"" (
                    ""Id""       INTEGER NOT NULL CONSTRAINT ""PK_DoorMaterials"" PRIMARY KEY AUTOINCREMENT,
                    ""Material"" TEXT    NOT NULL,
                    CONSTRAINT ""CK_DoorMaterial_Material"" CHECK (""Material"" IN ('Hollow Metal', 'Wood'))
                );

                INSERT INTO ""DoorMaterials_new"" (""Id"", ""Material"")
                    SELECT ""Id"",
                           CASE WHEN ""Material"" = 'Metal' THEN 'Hollow Metal' ELSE ""Material"" END
                    FROM ""DoorMaterials""
                    WHERE ""Material"" != 'Both';

                DROP TABLE ""DoorMaterials"";
                ALTER TABLE ""DoorMaterials_new"" RENAME TO ""DoorMaterials"";
            ");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }
    }
}
