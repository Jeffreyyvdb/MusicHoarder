using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlbumArtistFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlbumArtist",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalAlbumArtist",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "Songs"
                SET "AlbumArtist" = NULLIF(BTRIM(SPLIT_PART(COALESCE("Artist", ''), ';', 1)), '')
                WHERE "AlbumArtist" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DeletedAtUtc_AlbumArtist_Album_Year_Id",
                table: "Songs",
                columns: new[] { "DeletedAtUtc", "AlbumArtist", "Album", "Year", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_DeletedAtUtc_AlbumArtist_Album_Year_Id",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "AlbumArtist",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalAlbumArtist",
                table: "Songs");
        }
    }
}
