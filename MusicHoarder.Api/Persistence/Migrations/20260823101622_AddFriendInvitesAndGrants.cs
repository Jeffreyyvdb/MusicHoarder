using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendInvitesAndGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EmailNormalized = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryShareGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    GranteeUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ArtistKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AlbumKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ArtistDisplay = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    AlbumDisplay = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryShareGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invites_CreatedByUserId_RevokedAtUtc",
                table: "Invites",
                columns: new[] { "CreatedByUserId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Invites_EmailNormalized",
                table: "Invites",
                column: "EmailNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_Invites_TokenHash",
                table: "Invites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryShareGrants_GranteeUserId_RevokedAtUtc",
                table: "LibraryShareGrants",
                columns: new[] { "GranteeUserId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryShareGrants_OwnerUserId_GranteeUserId_RevokedAtUtc",
                table: "LibraryShareGrants",
                columns: new[] { "OwnerUserId", "GranteeUserId", "RevokedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Invites");

            migrationBuilder.DropTable(
                name: "LibraryShareGrants");
        }
    }
}
