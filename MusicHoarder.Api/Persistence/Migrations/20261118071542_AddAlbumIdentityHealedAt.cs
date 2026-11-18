using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumIdentityHealedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AlbumIdentityHealedAtUtc",
                table: "Songs",
                type: "timestamp with time zone",
                nullable: true);

            // Rows the album split healer corrected before the column existed, from its own change log. Only
            // where the heal is the latest word: a reset clears EnrichedAtUtc and a later match moves it
            // past the heal, and either one means the album is no longer the heal's.
            migrationBuilder.Sql(
                """
                UPDATE "Songs" AS s
                SET "AlbumIdentityHealedAtUtc" = c."HealedAt"
                FROM (
                    SELECT "SongId", MAX(COALESCE("AppliedAtUtc", "CreatedAtUtc")) AS "HealedAt"
                    FROM "SongMetadataChanges"
                    WHERE "Source" = 'album-identity-heal'
                    GROUP BY "SongId"
                ) AS c
                WHERE s."Id" = c."SongId" AND s."EnrichedAtUtc" <= c."HealedAt";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlbumIdentityHealedAtUtc",
                table: "Songs");
        }
    }
}
