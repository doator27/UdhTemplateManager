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
            // Intentionally left empty: the IsActive column on HardwareItems is
            // added idempotently by DatabaseInitializer.EnsureSchemaPatches
            // (guarded by ColumnExists), to avoid "duplicate column name" errors
            // on databases where this migration's history tracking is inconsistent
            // (see AddJobNotes migration for the same pattern).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
