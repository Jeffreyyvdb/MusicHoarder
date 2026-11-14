using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddArtistFollows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArtistCatalogLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ArtistKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    DeezerArtistId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PictureUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Fans = table.Column<int>(type: "integer", nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextCheckAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtistCatalogLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FollowedArtists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeezerArtistId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ArtistKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PictureUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ReleaseKinds = table.Column<int>(type: "integer", nullable: false),
                    AutoDownload = table.Column<bool>(type: "boolean", nullable: false),
                    FollowedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextCheckAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowedArtists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FollowedArtistReleases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FollowedArtistId = table.Column<int>(type: "integer", nullable: false),
                    ReleaseKey = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    DeezerAlbumId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CoverUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    TracksQueued = table.Column<int>(type: "integer", nullable: false),
                    CanonicalAlbumId = table.Column<int>(type: "integer", nullable: true),
                    DecidedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowedArtistReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FollowedArtistReleases_FollowedArtists_FollowedArtistId",
                        column: x => x.FollowedArtistId,
                        principalTable: "FollowedArtists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtistCatalogLinks_ArtistKey",
                table: "ArtistCatalogLinks",
                column: "ArtistKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowedArtistReleases_DecidedAtUtc",
                table: "FollowedArtistReleases",
                column: "DecidedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FollowedArtistReleases_FollowedArtistId_ReleaseKey",
                table: "FollowedArtistReleases",
                columns: new[] { "FollowedArtistId", "ReleaseKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowedArtists_NextCheckAtUtc",
                table: "FollowedArtists",
                column: "NextCheckAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FollowedArtists_OwnerUserId_ArtistKey",
                table: "FollowedArtists",
                columns: new[] { "OwnerUserId", "ArtistKey" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowedArtists_OwnerUserId_DeezerArtistId",
                table: "FollowedArtists",
                columns: new[] { "OwnerUserId", "DeezerArtistId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtistCatalogLinks");

            migrationBuilder.DropTable(
                name: "FollowedArtistReleases");

            migrationBuilder.DropTable(
                name: "FollowedArtists");
        }
    }
}
