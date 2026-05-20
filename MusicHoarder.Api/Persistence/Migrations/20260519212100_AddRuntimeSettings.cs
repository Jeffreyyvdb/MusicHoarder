using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuntimeSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RuntimeSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnableAcoustIdProvider = table.Column<bool>(type: "boolean", nullable: true),
                    EnableMusicBrainzWebProvider = table.Column<bool>(type: "boolean", nullable: true),
                    EnableSpotifyApiProvider = table.Column<bool>(type: "boolean", nullable: true),
                    EnableTrackerProvider = table.Column<bool>(type: "boolean", nullable: true),
                    SpotifyApiMatchedThreshold = table.Column<double>(type: "double precision", nullable: true),
                    AcoustIdScoreThreshold = table.Column<double>(type: "double precision", nullable: true),
                    EnrichmentWorkerConcurrency = table.Column<int>(type: "integer", nullable: true),
                    LibraryBuilderWorkerConcurrency = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuntimeSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RuntimeSettings");
        }
    }
}
