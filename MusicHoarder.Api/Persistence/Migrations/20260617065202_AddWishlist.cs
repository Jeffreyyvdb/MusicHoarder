using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWishlist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WishlistSources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    SpotifyPlaylistId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ImageUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    AutoSync = table.Column<bool>(type: "boolean", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishlistSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WishlistItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WishlistSourceId = table.Column<int>(type: "integer", nullable: true),
                    SpotifyTrackId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Artist = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Album = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Isrc = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    AlbumArt = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    SpotifyAddedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DownloadProvider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DownloadedFilePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    DownloadedSongId = table.Column<int>(type: "integer", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WishlistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WishlistItems_Songs_DownloadedSongId",
                        column: x => x.DownloadedSongId,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WishlistItems_WishlistSources_WishlistSourceId",
                        column: x => x.WishlistSourceId,
                        principalTable: "WishlistSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_DownloadedSongId",
                table: "WishlistItems",
                column: "DownloadedSongId");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_SpotifyTrackId",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "SpotifyTrackId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_Status",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_WishlistSourceId",
                table: "WishlistItems",
                column: "WishlistSourceId");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistSources_OwnerUserId_SourceType_SpotifyPlaylistId",
                table: "WishlistSources",
                columns: new[] { "OwnerUserId", "SourceType", "SpotifyPlaylistId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WishlistItems");

            migrationBuilder.DropTable(
                name: "WishlistSources");
        }
    }
}
