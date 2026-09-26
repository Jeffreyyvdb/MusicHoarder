using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubePlaylistSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "YouTubePlaylistId",
                table: "WishlistSources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YouTubeVideoId",
                table: "WishlistItems",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WishlistSources_OwnerUserId_SourceType_YouTubePlaylistId",
                table: "WishlistSources",
                columns: new[] { "OwnerUserId", "SourceType", "YouTubePlaylistId" },
                unique: true,
                filter: "\"YouTubePlaylistId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_YouTubeVideoId",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "YouTubeVideoId" },
                unique: true,
                filter: "\"YouTubeVideoId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WishlistSources_OwnerUserId_SourceType_YouTubePlaylistId",
                table: "WishlistSources");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_OwnerUserId_YouTubeVideoId",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "YouTubePlaylistId",
                table: "WishlistSources");

            migrationBuilder.DropColumn(
                name: "YouTubeVideoId",
                table: "WishlistItems");
        }
    }
}
