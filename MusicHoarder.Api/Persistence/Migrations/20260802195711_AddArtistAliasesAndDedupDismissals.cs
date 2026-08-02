using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArtistAliasesAndDedupDismissals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArtistAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AliasKey = table.Column<string>(type: "text", nullable: false),
                    CanonicalName = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistAliases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DedupDismissals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ScopeKey = table.Column<string>(type: "text", nullable: false),
                    KeyLow = table.Column<string>(type: "text", nullable: false),
                    KeyHigh = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DedupDismissals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistAliases_OwnerUserId_AliasKey",
                table: "ArtistAliases",
                columns: new[] { "OwnerUserId", "AliasKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DedupDismissals_OwnerUserId_Kind_ScopeKey_KeyLow_KeyHigh",
                table: "DedupDismissals",
                columns: new[] { "OwnerUserId", "Kind", "ScopeKey", "KeyLow", "KeyHigh" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistAliases");

            migrationBuilder.DropTable(
                name: "DedupDismissals");
        }
    }
}
