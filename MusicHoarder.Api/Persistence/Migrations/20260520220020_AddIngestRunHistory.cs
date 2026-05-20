using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIngestRunHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IngestRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SourcePath = table.Column<string>(type: "text", nullable: false),
                    DestinationPath = table.Column<string>(type: "text", nullable: false),
                    TracksDiscovered = table.Column<int>(type: "integer", nullable: false),
                    TracksProcessed = table.Column<int>(type: "integer", nullable: false),
                    TracksFingerprinted = table.Column<int>(type: "integer", nullable: false),
                    TracksEnriched = table.Column<int>(type: "integer", nullable: false),
                    TracksCopied = table.Column<int>(type: "integer", nullable: false),
                    TracksReview = table.Column<int>(type: "integer", nullable: false),
                    TracksFailed = table.Column<int>(type: "integer", nullable: false),
                    ThroughputPerSec = table.Column<double>(type: "double precision", nullable: false),
                    LogTailJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngestRuns_OwnerUserId_StartedAtUtc",
                table: "IngestRuns",
                columns: new[] { "OwnerUserId", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngestRuns");
        }
    }
}
