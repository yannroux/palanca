using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palanca.Data.Migrations
{
    /// <inheritdoc />
    public partial class ManagementTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TokenId",
                table: "ChangeRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ManagementTokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Hash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Permission = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AllApplications = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManagementTokenApplications",
                columns: table => new
                {
                    ApplicationId = table.Column<int>(type: "INTEGER", nullable: false),
                    TokenId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagementTokenApplications", x => new { x.ApplicationId, x.TokenId });
                    table.ForeignKey(
                        name: "FK_ManagementTokenApplications_Applications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManagementTokenApplications_ManagementTokens_TokenId",
                        column: x => x.TokenId,
                        principalTable: "ManagementTokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManagementTokenApplications_TokenId",
                table: "ManagementTokenApplications",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "IX_ManagementTokens_Prefix",
                table: "ManagementTokens",
                column: "Prefix");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManagementTokenApplications");

            migrationBuilder.DropTable(
                name: "ManagementTokens");

            migrationBuilder.DropColumn(
                name: "TokenId",
                table: "ChangeRecords");
        }
    }
}
