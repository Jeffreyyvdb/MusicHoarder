using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeezerDiscoverSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_OwnerUserId_SpotifyTrackId",
                table: "WishlistItems");

            migrationBuilder.AddColumn<string>(
                name: "DeezerPlaylistId",
                table: "WishlistSources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteChecksum",
                table: "WishlistSources",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SpotifyTrackId",
                table: "WishlistItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AddColumn<string>(
                name: "DeezerTrackId",
                table: "WishlistItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WishlistSources_OwnerUserId_SourceType_DeezerPlaylistId",
                table: "WishlistSources",
                columns: new[] { "OwnerUserId", "SourceType", "DeezerPlaylistId" },
                unique: true,
                filter: "\"DeezerPlaylistId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_DeezerTrackId",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "DeezerTrackId" },
                unique: true,
                filter: "\"DeezerTrackId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_SpotifyTrackId",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "SpotifyTrackId" },
                unique: true,
                filter: "\"SpotifyTrackId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WishlistSources_OwnerUserId_SourceType_DeezerPlaylistId",
                table: "WishlistSources");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_OwnerUserId_DeezerTrackId",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_OwnerUserId_SpotifyTrackId",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "DeezerPlaylistId",
                table: "WishlistSources");

            migrationBuilder.DropColumn(
                name: "RemoteChecksum",
                table: "WishlistSources");

            migrationBuilder.DropColumn(
                name: "DeezerTrackId",
                table: "WishlistItems");

            migrationBuilder.AlterColumn<string>(
                name: "SpotifyTrackId",
                table: "WishlistItems",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_SpotifyTrackId",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "SpotifyTrackId" },
                unique: true);
        }
    }
}
