using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGraderTrial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GraderComparisonRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubjectKind = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Arm = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FullStateBytes = table.Column<int>(type: "integer", nullable: true),
                    StateBytes = table.Column<int>(type: "integer", nullable: true),
                    StateShed = table.Column<bool>(type: "boolean", nullable: false),
                    Verdict = table.Column<int>(type: "integer", nullable: true),
                    Score = table.Column<int>(type: "integer", nullable: true),
                    FlagProbability = table.Column<double>(type: "double precision", nullable: true),
                    UnsupportedIdentity = table.Column<double>(type: "double precision", nullable: true),
                    IssueCodesJson = table.Column<string>(type: "text", nullable: true),
                    ProbabilitiesJson = table.Column<string>(type: "text", nullable: true),
                    Summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: true),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    CostUsd = table.Column<decimal>(type: "numeric(18,9)", precision: 18, scale: 9, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Error = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraderComparisonRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GraderTrialLabels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubjectKind = table.Column<int>(type: "integer", nullable: false),
                    SubjectId = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    InputFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<int>(type: "integer", nullable: false),
                    UnsupportedIdentity = table.Column<bool>(type: "boolean", nullable: false),
                    Stratum = table.Column<int>(type: "integer", nullable: false),
                    LabeledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraderTrialLabels", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GraderComparisonRuns_OwnerUserId_CreatedAtUtc",
                table: "GraderComparisonRuns",
                columns: new[] { "OwnerUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GraderComparisonRuns_SubjectKind_SubjectId_InputFingerprint~",
                table: "GraderComparisonRuns",
                columns: new[] { "SubjectKind", "SubjectId", "InputFingerprint", "Arm", "Model" });

            migrationBuilder.CreateIndex(
                name: "IX_GraderTrialLabels_OwnerUserId_SubjectKind_SubjectId_InputFi~",
                table: "GraderTrialLabels",
                columns: new[] { "OwnerUserId", "SubjectKind", "SubjectId", "InputFingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GraderComparisonRuns");

            migrationBuilder.DropTable(
                name: "GraderTrialLabels");
        }
    }
}
