using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palanca.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Applications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Applications", x => x.Id);
                    table.CheckConstraint("CK_Applications_Key", "length(\"Key\") BETWEEN 1 AND 64 AND \"Key\" GLOB '[a-z]*' AND \"Key\" NOT GLOB '*[^a-z0-9-]*'");
                });

            migrationBuilder.CreateTable(
                name: "ChangeRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ApplicationId = table.Column<int>(type: "INTEGER", nullable: true),
                    ApplicationKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: true),
                    EnvironmentKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FlagId = table.Column<int>(type: "INTEGER", nullable: true),
                    FlagKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OldValue = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeRecords", x => x.Id);
                });

            // Change records are append-only (FLG-043): the database refuses edits and deletions.
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_ChangeRecords_NoUpdate" BEFORE UPDATE ON "ChangeRecords"
                BEGIN SELECT RAISE(ABORT, 'Change records are append-only.'); END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_ChangeRecords_NoDelete" BEFORE DELETE ON "ChangeRecords"
                BEGIN SELECT RAISE(ABORT, 'Change records are append-only.'); END;
                """);

            migrationBuilder.CreateTable(
                name: "Environments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApplicationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsProtected = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Environments", x => x.Id);
                    table.CheckConstraint("CK_Environments_Key", "length(\"Key\") BETWEEN 1 AND 64 AND \"Key\" GLOB '[a-z]*' AND \"Key\" NOT GLOB '*[^a-z0-9-]*'");
                    table.ForeignKey(
                        name: "FK_Environments_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Flags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApplicationId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Flags", x => x.Id);
                    table.CheckConstraint("CK_Flags_Key", "length(\"Key\") BETWEEN 1 AND 64 AND \"Key\" GLOB '[a-z]*' AND \"Key\" NOT GLOB '*[^a-z0-9-]*'");
                    table.ForeignKey(
                        name: "FK_Flags_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SdkKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdkKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SdkKeys_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FlagValues",
                columns: table => new
                {
                    FlagId = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FlagValues", x => new { x.FlagId, x.EnvironmentId });
                    table.ForeignKey(
                        name: "FK_FlagValues_Environments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "Environments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FlagValues_Flags_FlagId",
                        column: x => x.FlagId,
                        principalTable: "Flags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Applications_Key",
                table: "Applications",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRecords_ApplicationId",
                table: "ChangeRecords",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_Environments_ApplicationId_Key",
                table: "Environments",
                columns: new[] { "ApplicationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlagValues_EnvironmentId",
                table: "FlagValues",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Flags_ApplicationId_Key",
                table: "Flags",
                columns: new[] { "ApplicationId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SdkKeys_EnvironmentId",
                table: "SdkKeys",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_SdkKeys_Prefix",
                table: "SdkKeys",
                column: "Prefix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeRecords");

            migrationBuilder.DropTable(
                name: "FlagValues");

            migrationBuilder.DropTable(
                name: "SdkKeys");

            migrationBuilder.DropTable(
                name: "Flags");

            migrationBuilder.DropTable(
                name: "Environments");

            migrationBuilder.DropTable(
                name: "Applications");
        }
    }
}
