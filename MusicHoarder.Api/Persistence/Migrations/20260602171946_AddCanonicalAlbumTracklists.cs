using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCanonicalAlbumTracklists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CanonicalAlbums",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ArtistKey = table.Column<string>(type: "text", nullable: false),
                    AlbumKey = table.Column<string>(type: "text", nullable: false),
                    DisplayTitle = table.Column<string>(type: "text", nullable: true),
                    DisplayArtist = table.Column<string>(type: "text", nullable: true),
                    Year = table.Column<int>(type: "integer", nullable: true),
                    CoverArtUrl = table.Column<string>(type: "text", nullable: true),
                    ResolvedTrackCount = table.Column<int>(type: "integer", nullable: false),
                    TrackCountContested = table.Column<bool>(type: "boolean", nullable: false),
                    SourcesJson = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRetryAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalAlbums", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CanonicalAlbumTracks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CanonicalAlbumId = table.Column<int>(type: "integer", nullable: false),
                    DiscNumber = table.Column<int>(type: "integer", nullable: false),
                    TrackNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    MusicBrainzRecordingId = table.Column<string>(type: "text", nullable: true),
                    CorroboratingProviders = table.Column<string>(type: "text", nullable: true),
                    CorroborationCount = table.Column<int>(type: "integer", nullable: false),
                    IsContested = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CanonicalAlbumTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CanonicalAlbumTracks_CanonicalAlbums_CanonicalAlbumId",
                        column: x => x.CanonicalAlbumId,
                        principalTable: "CanonicalAlbums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalAlbums_ArtistKey_AlbumKey",
                table: "CanonicalAlbums",
                columns: new[] { "ArtistKey", "AlbumKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalAlbums_Status_NextRetryAfterUtc",
                table: "CanonicalAlbums",
                columns: new[] { "Status", "NextRetryAfterUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalAlbumTracks_CanonicalAlbumId_DiscNumber_TrackNumber",
                table: "CanonicalAlbumTracks",
                columns: new[] { "CanonicalAlbumId", "DiscNumber", "TrackNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_CanonicalAlbumTracks_MusicBrainzRecordingId",
                table: "CanonicalAlbumTracks",
                column: "MusicBrainzRecordingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CanonicalAlbumTracks");

            migrationBuilder.DropTable(
                name: "CanonicalAlbums");
        }
    }
}
