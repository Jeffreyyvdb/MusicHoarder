using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Shared logic for community-tracker providers (unreleased / leaked files). Each tracker is a
/// single-artist database that mainstream catalogs (Spotify / MusicBrainz / AcoustID) don't cover,
/// so it's gated to a configured artist allowlist to avoid wasted calls and false matches on
/// unrelated music. Concrete subclasses pick the <see cref="Name"/>, <see cref="Priority"/>,
/// artist allowlist and backing <see cref="ITrackerCatalogService"/> (live API or local catalog) —
/// the matching/scoring is identical for every tracker.
/// </summary>
public abstract partial class CommunityTrackerEnrichmentProvider(
    ITrackerCatalogService catalog,
    IOptions<MusicEnricherOptions> options,
    ILogger logger) : IEnrichmentProvider
{
    private const double FuzzyThreshold = 85.0;

    /// <summary>Tracker availabilities meaning the song is documented but has never circulated.</summary>
    private static readonly string[] UnobtainableAvailabilities = ["confirmed", "rumored", "rumoured"];

    /// <summary>Tracker availabilities meaning only a fragment circulates, not the whole song.</summary>
    private static readonly string[] PartialAvailabilities =
        ["snippet", "partial", "beat only", "stem bounce", "tagged"];

    public abstract string Name { get; }
    public abstract int Priority { get; }

    /// <summary>Artist names/aliases this tracker covers; the gate opens only on a fuzzy match.</summary>
    protected abstract IReadOnlyList<string> ArtistAllowlist { get; }

    protected MusicEnricherOptions Options => options.Value;

    public bool CanHandle(SongMetadata song)
    {
        if (!SongSearchText.HasSearchableText(song, Options.SourceDirectory))
            return false;

        var (artist, _) = SongSearchText.Resolve(song, Options.SourceDirectory);
        return MatchesArtistAllowlist(artist);
    }

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var (_, effectiveTitle) = SongSearchText.Resolve(song, Options.SourceDirectory);
        if (string.IsNullOrWhiteSpace(effectiveTitle))
        {
            logger.LogDebug("{Provider} enrichment: no searchable title (SongId={SongId})", Name, song.Id);
            return new ProviderNoMatch();
        }

        IReadOnlyList<TrackerSong> candidates;
        try
        {
            candidates = await catalog.SearchAsync(effectiveTitle!, ct);
        }
        catch (ProviderRateLimitedException ex)
        {
            logger.LogWarning("{Provider} rate limited for song {SongId}, retry after {Delay}s",
                Name, song.Id, ex.RetryAfter.TotalSeconds);
            return new ProviderRateLimited(ex.RetryAfter);
        }

        logger.LogInformation(
            "{Provider} searched '{Query}' for SongId={SongId} → {CandidateCount} candidate(s)",
            Name, effectiveTitle, song.Id, candidates.Count);

        if (candidates.Count == 0)
            return new ProviderNoMatch();

        var opts = Options;
        TrackerSong? best = null;
        double bestScore = 0;
        double bestDurationDelta = double.PositiveInfinity;
        var bestAvailabilityRank = int.MaxValue;
        var bestWarnings = new List<string>();

        foreach (var candidate in candidates)
        {
            // An entry the tracker marks as never-leaked documents that the song exists; no file on
            // disk can be it. Scoring it would only ever produce a false positive.
            if (opts.TrackerSkipUnobtainable && IsUnobtainable(candidate))
                continue;

            var (score, warnings, durationDelta) = ScoreCandidate(song, effectiveTitle, candidate, opts);
            // Preference among candidates that tie on score and length: a real, fully-circulating
            // entry beats a fragment, and anything real beats a known AI fake.
            var availabilityRank = (candidate.IsAiGenerated ? 2 : 0) + (IsPartial(candidate) ? 1 : 0);

            // Highest title agreement wins. But a single song often has many versions with the same
            // title ([V1], [V2], …) that tie on title score — so the length closest to the file is the
            // tiebreaker that picks the right version (a candidate with no length sorts last). When
            // neither carries a length, prefer the one that circulates in full over a snippet.
            var better = score > bestScore + 1e-9
                || (score > bestScore - 1e-9
                    && (durationDelta < bestDurationDelta
                        || (durationDelta.Equals(bestDurationDelta) && availabilityRank < bestAvailabilityRank)));
            if (better)
            {
                bestScore = score;
                best = candidate;
                bestWarnings = warnings;
                bestDurationDelta = durationDelta;
                bestAvailabilityRank = availabilityRank;
            }
        }

        if (best is null)
            return new ProviderNoMatch();

        // The tracker lists this recording as an AI fake. Surfacing it is the point — it tells you
        // what the file actually is — but it must never be applied as though it were real
        // metadata, so it always lands in review however well it scored.
        if (best.IsAiGenerated)
            bestWarnings.Add("ai_generated");

        if (bestScore < opts.TrackerMinConfidence - 1e-9)
            return new ProviderNoMatch(BuildResult(song, best, bestScore, bestWarnings, EnrichmentStatus.NeedsReview));

        var blocking = HasBlockingWarning(bestWarnings);
        var status = bestScore >= opts.TrackerMatchedThreshold - 1e-9 && !blocking
            ? EnrichmentStatus.Matched
            : EnrichmentStatus.NeedsReview;

        return new ProviderMatched(BuildResult(song, best, bestScore, bestWarnings, status));
    }

    private bool MatchesArtistAllowlist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;

        foreach (var allowed in ArtistAllowlist)
        {
            if (FuzzyTextMatch.Ratio(artist, allowed) is double ratio && ratio >= Options.IdentityArtistThreshold)
                return true;
        }

        return false;
    }

    private EnrichmentProviderResult BuildResult(
        SongMetadata song,
        TrackerSong track,
        double score,
        List<string> warnings,
        EnrichmentStatus status)
    {
        // Keep the song's existing (allowlisted) artist when it has one; only fall back to the
        // tracker's credit for otherwise-untagged files. Artists (the discrete per-artist frame) is
        // deliberately left unset: the tracker only publishes a combined credit
        // ("Ty Dolla $ign & Lil Durk"), and there's no split that survives names containing a comma.
        var artist = string.IsNullOrWhiteSpace(song.Artist)
            ? ArtistCreditNormalizer.NormalizeDisplayCredit(BuildTrackerCredit(track))
            : song.Artist;
        // Album-artist is album-level — don't derive it from the track credit (a featured guest on
        // compilations/collabs, and comma-names get truncated). Keep the song's curated album-artist;
        // fall back to the track's primary artist only for genuinely untagged files.
        var albumArtist = !string.IsNullOrWhiteSpace(song.AlbumArtist)
            ? song.AlbumArtist
            : ArtistCreditNormalizer.GetPrimaryArtist(artist) ?? artist;

        var resultWarnings = new List<string>(warnings);
        if (!string.IsNullOrWhiteSpace(track.Category))
            resultWarnings.Add($"category:{track.Category}");

        return new EnrichmentProviderResult(
            Artist: artist,
            AlbumArtist: albumArtist,
            Title: string.IsNullOrWhiteSpace(track.Name) ? song.Title : track.Name,
            Year: track.Year,
            TrackNumber: null,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: string.IsNullOrWhiteSpace(track.SpotifyId) ? null : track.SpotifyId,
            AcoustIdTrackId: null,
            Isrc: null,
            MatchedBy: Name,
            MatchConfidence: Math.Clamp(score, 0, 1),
            MatchWarnings: resultWarnings,
            RecommendedStatus: status,
            Album: ResolveAlbum(track));
    }

    /// <summary>
    /// The album to attribute the match to. A tracker era doubles as the album name for the
    /// artist's own material, but a "Feature"/"Production" credit appears on <i>somebody else's</i>
    /// record — there the era is only Ye's career period, and using it as the album would file the
    /// track under a release it isn't on.
    /// </summary>
    private static string? ResolveAlbum(TrackerSong track)
    {
        if (string.IsNullOrWhiteSpace(track.Era))
            return null;
        if (track.TrackType is "Feature" or "Production")
            return null;
        return track.Era;
    }

    /// <summary>The tracker's combined display credit, guest included ("Ye feat. Ty Dolla $ign").</summary>
    private static string? BuildTrackerCredit(TrackerSong track)
    {
        if (string.IsNullOrWhiteSpace(track.CreditedArtists))
            return track.Featured;
        if (string.IsNullOrWhiteSpace(track.Featured))
            return track.CreditedArtists;
        return $"{track.CreditedArtists} feat. {track.Featured}";
    }

    private static (double Score, List<string> Warnings, double DurationDelta) ScoreCandidate(
        SongMetadata song,
        string? sourceTitle,
        TrackerSong track,
        MusicEnricherOptions opts)
    {
        var warnings = new List<string>();

        // The DB is single-artist (and artist agreement was already established by the allowlist
        // gate), and leaks are heavily aliased — so the match is driven by the best title agreement
        // across the canonical name and every alias track title.
        double? bestTitleRatio = FuzzyTextMatch.Ratio(sourceTitle, track.Name);
        foreach (var alias in track.TrackTitles)
        {
            if (FuzzyTextMatch.Ratio(sourceTitle, alias) is double r &&
                (bestTitleRatio is not double current || r > current))
            {
                bestTitleRatio = r;
            }
        }

        // A leak usually circulates under its OG filename, so an untagged file is often named that
        // and nothing else. When the filename beats every title alias it stands in for the title —
        // it's a weaker identity, so it has to clear a near-exact bar of its own.
        var ogRatio = BestOgFilenameRatio(song, sourceTitle, track);
        var matchedViaOgFilename = ogRatio is double og
            && og >= opts.TrackerOgFilenameMinRatio
            && (bestTitleRatio is not double t || og > t);
        if (matchedViaOgFilename)
        {
            bestTitleRatio = ogRatio;
            warnings.Add("matched_via_og_filename");
        }

        if (bestTitleRatio is not double titleRatio)
            return (0, ["title_unknown"], double.PositiveInfinity);

        if (titleRatio < FuzzyThreshold)
            warnings.Add("title_mismatch");

        var score = titleRatio / 100.0;

        // Version markers are the tracker's own disambiguator between same-title takes; when both
        // sides state one, agreement is strong evidence and disagreement is strong counter-evidence.
        var sourceVersion = ExtractVersion(song.Title) ?? ExtractVersion(sourceTitle) ?? ExtractVersion(song.FileName);
        if (sourceVersion is int sv && track.Version is int tv)
        {
            if (sv == tv)
            {
                score = Math.Min(1.0, score + opts.TrackerVersionMatchBonus);
            }
            else
            {
                warnings.Add("version_number_mismatch");
                score *= opts.TrackerVersionMismatchPenalty;
            }
        }

        // Distance (seconds) between the file and this candidate; PositiveInfinity when either side
        // has no length, so length-less candidates lose the version tiebreak to ones that match.
        var durationDelta = double.PositiveInfinity;
        var songDurationSec = song.DurationSeconds
            ?? (song.DurationMs is int ms ? ms / 1000.0 : (double?)null);
        if (songDurationSec is not null && track.DurationSeconds is double trackDuration && trackDuration > 0)
        {
            durationDelta = Math.Abs(songDurationSec.Value - trackDuration);
            if (durationDelta > opts.SpotifyApiDurationDeltaThresholdSeconds)
            {
                warnings.Add("duration_mismatch");
                score *= opts.SpotifyApiDurationMismatchPenalty;
            }
        }

        // Keep a "Live"/"Remix"/"Instrumental" alias from satisfying a request for the studio cut.
        var sourceQual = VersionQualifier.Detect(song.Title, song.Album);
        var candQual = VersionQualifier.Detect(track.Name);
        if (!VersionQualifier.Compare(sourceQual, candQual))
        {
            warnings.Add("version_mismatch");
            score *= 0.6;
        }

        return (Math.Clamp(score, 0, 1), warnings, durationDelta);
    }

    /// <summary>True when the tracker says this entry has never leaked (so no file can be it).</summary>
    private static bool IsUnobtainable(TrackerSong track) =>
        MatchesAvailability(track, UnobtainableAvailabilities);

    /// <summary>True when only a fragment of this entry circulates (snippet, stem, beat-only, …).</summary>
    private static bool IsPartial(TrackerSong track) =>
        MatchesAvailability(track, PartialAvailabilities);

    private static bool MatchesAvailability(TrackerSong track, string[] values)
    {
        if (string.IsNullOrWhiteSpace(track.Availability))
            return false;
        var availability = track.Availability.Trim();
        return Array.Exists(values, v => availability.Equals(v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Best agreement between one of the tracker's OG filenames and the text we know the file by —
    /// its resolved title and, more tellingly, the filename on disk.
    /// </summary>
    private static double? BestOgFilenameRatio(SongMetadata song, string? sourceTitle, TrackerSong track)
    {
        if (track.OgFilenames is not { Count: > 0 } ogFilenames)
            return null;

        double? best = null;
        foreach (var text in (ReadOnlySpan<string?>)[sourceTitle, Path.GetFileNameWithoutExtension(song.FileName)])
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            foreach (var ogFilename in ogFilenames)
            {
                if (FuzzyTextMatch.Ratio(text, ogFilename) is double ratio && (best is not double b || ratio > b))
                    best = ratio;
            }
        }
        return best;
    }

    /// <summary>The <c>[Vn]</c> ordinal in a title or filename, or null when it carries none.</summary>
    private static int? ExtractVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = VersionMarkerPattern().Match(text);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, out var version) ? version : null;
    }

    [GeneratedRegex(@"\[[Vv](\d{1,3})\b", RegexOptions.Compiled)]
    private static partial Regex VersionMarkerPattern();

    private static bool HasBlockingWarning(List<string> warnings) =>
        warnings.Exists(static w =>
            w is "title_mismatch" or "title_unknown" or "duration_mismatch" or "version_mismatch" or "ai_generated");
}
