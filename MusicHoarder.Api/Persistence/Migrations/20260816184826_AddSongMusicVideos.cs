using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSongMusicVideos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DownloadedVideoFilePath",
                table: "WishlistItems",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DownloadedVideoIsSameSource",
                table: "WishlistItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DownloadedVideoYouTubeId",
                table: "WishlistItems",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SongMusicVideos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    FilePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    YouTubeVideoId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    SyncOffsetMs = table.Column<int>(type: "integer", nullable: false),
                    SyncSource = table.Column<int>(type: "integer", nullable: false),
                    SyncConfidence = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    FetchedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongMusicVideos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SongMusicVideos_Songs_SongId",
                        column: x => x.SongId,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SongMusicVideos_SongId",
                table: "SongMusicVideos",
                column: "SongId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SongMusicVideos");

            migrationBuilder.DropColumn(
                name: "DownloadedVideoFilePath",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "DownloadedVideoIsSameSource",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "DownloadedVideoYouTubeId",
                table: "WishlistItems");
        }
    }
}
