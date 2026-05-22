using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaggingOrganizationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlbumArtistMusicBrainzId",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtistMusicBrainzIds",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Artists",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscNumber",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCompilation",
                table: "Songs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzReleaseGroupId",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalArtists",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalDiscNumber",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OriginalIsCompilation",
                table: "Songs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OriginalReleaseTypePrimary",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalReleaseTypes",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalTotalDiscs",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalTotalTracks",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseTypePrimary",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseTypes",
                table: "Songs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalDiscs",
                table: "Songs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TotalTracks",
                table: "Songs",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlbumArtistMusicBrainzId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ArtistMusicBrainzIds",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "Artists",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "DiscNumber",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "IsCompilation",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "MusicBrainzReleaseGroupId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalArtists",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalDiscNumber",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalIsCompilation",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalReleaseTypePrimary",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalReleaseTypes",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalTotalDiscs",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "OriginalTotalTracks",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ReleaseTypePrimary",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ReleaseTypes",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TotalDiscs",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "TotalTracks",
                table: "Songs");
        }
    }
}
