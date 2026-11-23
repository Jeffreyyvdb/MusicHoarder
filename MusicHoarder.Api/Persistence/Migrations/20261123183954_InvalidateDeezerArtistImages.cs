using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MusicHoarder.Api.Persistence.Migrations;

/// <summary>
/// Data-only invalidation after switching artist portrait priority from Deezer → Spotify to
/// Spotify → Deezer. Stamps existing non-Spotify rows with the Unix epoch sentinel so
/// <see cref="Artwork.ArtistImageService"/> refreshes them from Spotify on next lookup while
/// keeping their current URL intact as fallback if Spotify cannot answer or has no match.
/// </summary>
public partial class _20261123183954_InvalidateDeezerArtistImages : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "ArtistImages"
            SET "FetchedAtUtc" = TIMESTAMPTZ '1970-01-01 00:00:00+00'
            WHERE "ImageUrl" IS NOT NULL
              AND ("Source" IS NULL OR "Source" <> 'spotify');
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Data invalidation: no schema change to revert.
    }
}
