using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DownloadedSourceId",
                table: "WishlistItems",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SongReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: true),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Artist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Album = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MatchedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MatchConfidence = table.Column<double>(type: "double precision", nullable: true),
                    WishlistItemId = table.Column<int>(type: "integer", nullable: true),
                    Origin = table.Column<int>(type: "integer", nullable: true),
                    RequestedTitle = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RequestedArtist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RequestedAlbum = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RequestedDurationMs = table.Column<int>(type: "integer", nullable: true),
                    SpotifyTrackId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeezerTrackId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Isrc = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DownloadProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DownloadedSourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    RemovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongReports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongReports_OwnerUserId_CreatedAtUtc",
                table: "SongReports",
                columns: new[] { "OwnerUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SongReports_SongId",
                table: "SongReports",
                column: "SongId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongReports");

            migrationBuilder.DropColumn(
                name: "DownloadedSourceId",
                table: "WishlistItems");
        }
    }
}
