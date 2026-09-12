using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations
{
    /// <summary>
    /// Data-only heal for rows the library builder quarantined for an enrichment-gate skip rather than
    /// a file problem. Between #330 (which started persisting "not buildable at build time" as a build
    /// failure) and #331 (which aligned the builder's guard with the batch query fifteen minutes later)
    /// every NeedsReview candidate was rejected on each sweep until it hit MaxLibraryBuildAttempts, and
    /// nothing on the way back to Matched resets that counter — so a later match could never build.
    /// The builder no longer records the gate skip at all; this puts the legacy rows back to Pending
    /// (attempts zeroed) so the next sweep picks up the ones that are Matched and the rest simply wait
    /// for enrichment. Genuine file failures (corrupt audio, un-writable destination) carry a different
    /// error and are left quarantined. The prefix match is the only signal the legacy rows left, and it
    /// lives only here: the string is never written again.
    /// </summary>
    public partial class HealEnrichmentGateBuildFailures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // LibraryBuildStatus: 0 = Pending, 4 = Failed. Same field set ResetLibraryBuild/RequeueForRetag
            // clear, minus the destination paths (kept: a re-tag-flagged row must not lose its file).
            migrationBuilder.Sql(
                """
                UPDATE "Songs"
                SET "LibraryBuildStatus" = 0,
                    "LibraryBuildAttempts" = 0,
                    "LibraryBuildError" = NULL,
                    "LibraryBuildLastAttemptedAtUtc" = NULL
                WHERE "LibraryBuildStatus" = 4
                  AND "LibraryBuildError" LIKE 'Not buildable at build time%';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data heal: no schema change to revert.
        }
    }
}
