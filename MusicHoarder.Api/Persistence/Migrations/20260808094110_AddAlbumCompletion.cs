using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <summary>
    /// Album completion: the sweep's per-owner marker table, the wishlist-item origin discriminator and
    /// its album back-reference, and the song-level acquisition intent that "My music" filters on.
    /// <para>
    /// No data backfill, by construction. Both new enum columns default to 0 — <c>Explicit</c> for a
    /// song, <c>UserRequested</c> for a wishlist item — so every existing row is already correct: all
    /// current music is the owner's, and all queued items keep top download priority. That is the whole
    /// reason those enum members are ordered the way they are.
    /// </para>
    /// </summary>
    public partial class AddAlbumCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_OwnerUserId_Status",
                table: "WishlistItems");

            migrationBuilder.AddColumn<int>(
                name: "CanonicalAlbumId",
                table: "WishlistItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Origin",
                table: "WishlistItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AcquisitionIntent",
                table: "Songs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "AlbumCompletionEnabled",
                table: "RuntimeSettings",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlbumCompletionStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalAlbumId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LastSweptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextSweepAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OwnedTrackCount = table.Column<int>(type: "integer", nullable: false),
                    CanonicalTrackCount = table.Column<int>(type: "integer", nullable: false),
                    EnqueuedTrackCount = table.Column<int>(type: "integer", nullable: false),
                    SkipReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumCompletionStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlbumCompletionStates_CanonicalAlbums_CanonicalAlbumId",
                        column: x => x.CanonicalAlbumId,
                        principalTable: "CanonicalAlbums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_CanonicalAlbumId",
                table: "WishlistItems",
                column: "CanonicalAlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_CanonicalAlbumId",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "CanonicalAlbumId" });

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_Status_Origin",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "Status", "Origin" });

            migrationBuilder.CreateIndex(
                name: "IX_Songs_OwnerUserId_AcquisitionIntent",
                table: "Songs",
                columns: new[] { "OwnerUserId", "AcquisitionIntent" });

            migrationBuilder.CreateIndex(
                name: "IX_AlbumCompletionStates_CanonicalAlbumId",
                table: "AlbumCompletionStates",
                column: "CanonicalAlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_AlbumCompletionStates_OwnerUserId_CanonicalAlbumId",
                table: "AlbumCompletionStates",
                columns: new[] { "OwnerUserId", "CanonicalAlbumId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlbumCompletionStates_OwnerUserId_NextSweepAfterUtc",
                table: "AlbumCompletionStates",
                columns: new[] { "OwnerUserId", "NextSweepAfterUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_WishlistItems_CanonicalAlbums_CanonicalAlbumId",
                table: "WishlistItems",
                column: "CanonicalAlbumId",
                principalTable: "CanonicalAlbums",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WishlistItems_CanonicalAlbums_CanonicalAlbumId",
                table: "WishlistItems");

            migrationBuilder.DropTable(
                name: "AlbumCompletionStates");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_CanonicalAlbumId",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_OwnerUserId_CanonicalAlbumId",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_WishlistItems_OwnerUserId_Status_Origin",
                table: "WishlistItems");

            migrationBuilder.DropIndex(
                name: "IX_Songs_OwnerUserId_AcquisitionIntent",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "CanonicalAlbumId",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "WishlistItems");

            migrationBuilder.DropColumn(
                name: "AcquisitionIntent",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "AlbumCompletionEnabled",
                table: "RuntimeSettings");

            migrationBuilder.CreateIndex(
                name: "IX_WishlistItems_OwnerUserId_Status",
                table: "WishlistItems",
                columns: new[] { "OwnerUserId", "Status" });
        }
    }
}
