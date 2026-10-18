using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Palanca.Data.Migrations
{
    /// <inheritdoc />
    public partial class OwnerSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Owners",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Owners", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OwnerEnrollments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hash = table.Column<byte[]>(type: "BLOB", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerEnrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerEnrollments_Owners_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Owners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OwnerPasskeys",
                columns: table => new
                {
                    CredentialId = table.Column<byte[]>(type: "BLOB", maxLength: 1023, nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Data = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerPasskeys", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_OwnerPasskeys_Owners_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Owners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerEnrollments_Hash",
                table: "OwnerEnrollments",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerEnrollments_OwnerId",
                table: "OwnerEnrollments",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerPasskeys_OwnerId",
                table: "OwnerPasskeys",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Owners_Email",
                table: "Owners",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerEnrollments");

            migrationBuilder.DropTable(
                name: "OwnerPasskeys");

            migrationBuilder.DropTable(
                name: "Owners");
        }
    }
}
