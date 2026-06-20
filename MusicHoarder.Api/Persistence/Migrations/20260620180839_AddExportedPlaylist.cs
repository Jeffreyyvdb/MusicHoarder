using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExportedPlaylist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExportedPlaylists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SpotifyPlaylistId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    SpotifyTrackTotal = table.Column<int>(type: "integer", nullable: false),
                    MatchedTrackCount = table.Column<int>(type: "integer", nullable: false),
                    LastGeneratedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExportedPlaylists", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExportedPlaylists_OwnerUserId_Kind_SpotifyPlaylistId",
                table: "ExportedPlaylists",
                columns: new[] { "OwnerUserId", "Kind", "SpotifyPlaylistId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExportedPlaylists");
        }
    }
}
