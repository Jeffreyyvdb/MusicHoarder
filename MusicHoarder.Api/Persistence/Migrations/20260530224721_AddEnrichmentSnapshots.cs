using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEnrichmentSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnrichmentSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    TriggerLabel = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConfigJson = table.Column<string>(type: "text", nullable: false),
                    ConfigHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TotalSongs = table.Column<int>(type: "integer", nullable: false),
                    MatchedCount = table.Column<int>(type: "integer", nullable: false),
                    NeedsReviewCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    PendingCount = table.Column<int>(type: "integer", nullable: false),
                    DuplicateCount = table.Column<int>(type: "integer", nullable: false),
                    BuildDoneCount = table.Column<int>(type: "integer", nullable: false),
                    AvgMatchConfidence = table.Column<double>(type: "double precision", nullable: true),
                    ProviderMatchedJson = table.Column<string>(type: "text", nullable: true),
                    GradedCount = table.Column<int>(type: "integer", nullable: false),
                    AvgAiScore = table.Column<double>(type: "double precision", nullable: true),
                    AiExcellent = table.Column<int>(type: "integer", nullable: false),
                    AiGood = table.Column<int>(type: "integer", nullable: false),
                    AiQuestionable = table.Column<int>(type: "integer", nullable: false),
                    AiWrong = table.Column<int>(type: "integer", nullable: false),
                    AiUngradeable = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrichmentSnapshotSongs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SnapshotId = table.Column<int>(type: "integer", nullable: false),
                    SongId = table.Column<int>(type: "integer", nullable: false),
                    EnrichmentStatus = table.Column<int>(type: "integer", nullable: false),
                    MatchConfidence = table.Column<double>(type: "double precision", nullable: true),
                    MatchedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsDuplicate = table.Column<bool>(type: "boolean", nullable: false),
                    AiScore = table.Column<int>(type: "integer", nullable: true),
                    AiVerdict = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrichmentSnapshotSongs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnrichmentSnapshotSongs_EnrichmentSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "EnrichmentSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentSnapshots_OwnerUserId_CapturedAtUtc",
                table: "EnrichmentSnapshots",
                columns: new[] { "OwnerUserId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrichmentSnapshotSongs_SnapshotId_SongId",
                table: "EnrichmentSnapshotSongs",
                columns: new[] { "SnapshotId", "SongId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnrichmentSnapshotSongs");

            migrationBuilder.DropTable(
                name: "EnrichmentSnapshots");
        }
    }
}
