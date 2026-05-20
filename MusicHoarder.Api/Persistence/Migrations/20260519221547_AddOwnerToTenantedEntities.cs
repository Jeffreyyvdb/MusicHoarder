using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnerToTenantedEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SpotifyTrackLibraryMatches",
                table: "SpotifyTrackLibraryMatches");

            migrationBuilder.DropIndex(
                name: "IX_Songs_SourcePath",
                table: "Songs");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "SpotifyTrackLibraryMatches",
                type: "uuid",
                nullable: false,
                // Backfill existing rows with the well-known Owner GUID seeded in AddAuthSchema.
                // See MusicHoarder.Api.Auth.WellKnownUsers.OwnerId. Do not change this value.
                defaultValue: new Guid("9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f"));

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "SpotifySettings",
                type: "uuid",
                nullable: false,
                // Backfill existing rows with the well-known Owner GUID seeded in AddAuthSchema.
                // See MusicHoarder.Api.Auth.WellKnownUsers.OwnerId. Do not change this value.
                defaultValue: new Guid("9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f"));

            migrationBuilder.AddColumn<bool>(
                name: "IsSynthetic",
                table: "Songs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Songs",
                type: "uuid",
                nullable: false,
                // Backfill existing rows with the well-known Owner GUID seeded in AddAuthSchema.
                // See MusicHoarder.Api.Auth.WellKnownUsers.OwnerId. Do not change this value.
                defaultValue: new Guid("9c0f1e3d-7b6a-4d2e-9c8f-0a1b2c3d4e5f"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_SpotifyTrackLibraryMatches",
                table: "SpotifyTrackLibraryMatches",
                columns: new[] { "OwnerUserId", "SpotifyTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_SpotifySettings_OwnerUserId",
                table: "SpotifySettings",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_OwnerUserId_DeletedAtUtc",
                table: "Songs",
                columns: new[] { "OwnerUserId", "DeletedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Songs_OwnerUserId_SourcePath",
                table: "Songs",
                columns: new[] { "OwnerUserId", "SourcePath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SpotifyTrackLibraryMatches",
                table: "SpotifyTrackLibraryMatches");

            migrationBuilder.DropIndex(
                name: "IX_SpotifySettings_OwnerUserId",
                table: "SpotifySettings");

            migrationBuilder.DropIndex(
                name: "IX_Songs_OwnerUserId_DeletedAtUtc",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_OwnerUserId_SourcePath",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "SpotifyTrackLibraryMatches");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "SpotifySettings");

            migrationBuilder.DropColumn(
                name: "IsSynthetic",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Songs");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SpotifyTrackLibraryMatches",
                table: "SpotifyTrackLibraryMatches",
                column: "SpotifyTrackId");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_SourcePath",
                table: "Songs",
                column: "SourcePath",
                unique: true);
        }
    }
}
