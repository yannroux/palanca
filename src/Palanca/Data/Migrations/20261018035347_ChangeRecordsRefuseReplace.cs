using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palanca.Data.Migrations
{
    /// <inheritdoc />
    public partial class ChangeRecordsRefuseReplace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // INSERT OR REPLACE removes the old row without firing delete triggers, so an insert that reuses an
            // existing record's Id is refused too (FLG-043).
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_ChangeRecords_NoReplace" BEFORE INSERT ON "ChangeRecords"
                WHEN EXISTS (SELECT 1 FROM "ChangeRecords" WHERE "Id" = NEW."Id")
                BEGIN SELECT RAISE(ABORT, 'Change records are append-only.'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER "TR_ChangeRecords_NoReplace";""");
        }
    }
}
