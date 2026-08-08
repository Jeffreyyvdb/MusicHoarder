using MusicHoarder.Api.Library;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>The owned-song facts one album group contributes to the eligibility decision.</summary>
public readonly record struct AlbumCompletionCandidate(
    string? AlbumArtist,
    string? Artist,
    bool IsCompilation,
    string? ReleaseTypes);

/// <summary>
/// Decides whether an album the owner partly holds may be auto-completed.
/// <para>
/// Every rule here exists to stop one concrete failure: <see cref="CanonicalAlbumTrack"/> carries no
/// per-track artist, so a filled track is always searched under the <em>album</em> artist. On a real
/// album that is correct. On a compilation it means asking the downloader for
/// "Various Artists — Some Song", which fetches whatever it finds.
/// </para>
/// </summary>
public static class AlbumCompletionEligibility
{
    public const string ReasonVariousArtists = "various-artists";
    public const string ReasonCompilationReleaseType = "compilation-release-type";
    public const string ReasonArtistMismatch = "artist-mismatch";
    public const string ReasonTooFewCanonicalTracks = "too-few-canonical-tracks";

    /// <summary>
    /// Returns null when the album may be completed, otherwise the reason it was skipped (one of the
    /// <c>Reason*</c> constants, stored on <see cref="AlbumCompletionState.SkipReason"/>).
    /// </summary>
    public static string? Skip(
        IReadOnlyCollection<AlbumCompletionCandidate> owned,
        CanonicalAlbum canonical,
        MusicEnricherOptions options)
    {
        if (canonical.Tracks.Count < options.AlbumCompletionMinCanonicalTracks)
            return ReasonTooFewCanonicalTracks;

        // The house Various-Artists rule, so folder routing and completion can never disagree about
        // what a compilation is. Note it deliberately does NOT catch a single-artist greatest-hits that
        // merely carries the compilation flag — that one is caught by the release-type rule below.
        var albumArtist = owned.Select(o => o.AlbumArtist).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (DestinationPathResolver.IsVariousArtists(owned.Any(o => o.IsCompilation), albumArtist))
            return ReasonVariousArtists;

        // Local tags can be wrong where the reconciled providers are right.
        if (!string.IsNullOrWhiteSpace(canonical.DisplayArtist)
            && DestinationPathResolver.IsVariousArtistsSentinel(canonical.DisplayArtist))
            return ReasonVariousArtists;

        // MusicBrainz release-group type, e.g. "album;compilation" (lowercased at write time).
        if (options.AlbumCompletionSkipReleaseTypes.Length > 0)
        {
            var typed = owned.Where(o => !string.IsNullOrWhiteSpace(o.ReleaseTypes)).ToList();
            if (typed.Count > 0)
            {
                var flagged = typed.Count(o => MultiValue.Split(o.ReleaseTypes)
                    .Any(t => options.AlbumCompletionSkipReleaseTypes
                        .Any(skip => string.Equals(t, skip, StringComparison.OrdinalIgnoreCase))));
                if (flagged * 2 > typed.Count)
                    return ReasonCompilationReleaseType;
            }
        }

        // The guard that catches the nastiest case. A compilation ingested with no album artist groups
        // by *track* artist, so it shatters into one plausible-looking single-artist album per
        // contributor — each with a track or two owned. Without this, the sweep would queue the whole
        // compilation once per contributor, every track searched under the wrong artist. Requiring the
        // canonical album's artist to resemble the group's artist kills that, and the generic
        // "two different albums share a title" collision with it.
        var groupArtist = albumArtist ?? owned.Select(o => o.Artist).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        var ratio = FuzzyTextMatch.Ratio(canonical.DisplayArtist, groupArtist);
        if (ratio is { } r && r < options.IdentityTitleThreshold)
            return ReasonArtistMismatch;

        return null;
    }
}
