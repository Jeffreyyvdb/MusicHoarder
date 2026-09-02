using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Scanner;

/// <summary>
/// Stage one of duplicate detection: proposes candidate pairs among one owner's songs by blocking
/// on exact fingerprint equality, shared AcoustID track id, shared ISRC, and normalized
/// primary-artist + title — each with its own guards (version-qualifier compatibility, duration
/// tolerance). A pair found by more than one strategy carries every reason that found it.
/// <para>
/// Pure over the songs it is given: no audio is decoded and nothing is persisted. Whether a
/// candidate is actually the same recording is <see cref="DuplicatePairConfirmer"/>'s call.
/// </para>
/// </summary>
public sealed class DuplicateCandidateGenerator(ILogger<DuplicateCandidateGenerator> logger)
{
    public Dictionary<SongIdPair, DuplicateMatchReason> Generate(
        Guid ownerId,
        IReadOnlyList<SongMetadata> ownerSongs,
        MusicEnricherOptions opts)
    {
        var candidates = new Dictionary<SongIdPair, DuplicateMatchReason>();

        void AddPair(SongMetadata a, SongMetadata b, DuplicateMatchReason reason)
        {
            var key = SongIdPair.Of(a.Id, b.Id);
            candidates[key] = candidates.GetValueOrDefault(key) | reason;
        }

        // Live/remix/acoustic/etc. never pairs with the studio recording, no matter how well the
        // normalized text or identifiers agree (compilations reuse ISRCs across masters).
        static bool QualifiersCompatible(SongMetadata a, SongMetadata b) =>
            VersionQualifier.Compare(VersionQualifier.Detect(a.Title), VersionQualifier.Detect(b.Title));

        static bool DurationsWithin(SongMetadata a, SongMetadata b, int toleranceSeconds, bool requireBoth)
        {
            if (a.DurationSeconds is not int da || b.DurationSeconds is not int db)
                return !requireBoth;
            return Math.Abs(da - db) <= toleranceSeconds;
        }

        void AddGroupPairs(
            IEnumerable<IGrouping<string, SongMetadata>> groups,
            DuplicateMatchReason reason,
            string blockKind,
            Func<SongMetadata, SongMetadata, bool> pairGuard)
        {
            foreach (var group in groups)
            {
                var members = group.ToList();
                if (members.Count < 2)
                    continue;

                if (members.Count > opts.DuplicateMaxBlockSize)
                {
                    logger.LogWarning(
                        "Skipping pathological duplicate-candidate block ({Kind}, {Count} songs, owner {OwnerUserId}): key {Key}",
                        blockKind, members.Count, ownerId, group.Key);
                    continue;
                }

                for (var i = 0; i < members.Count; i++)
                    for (var j = i + 1; j < members.Count; j++)
                        if (pairGuard(members[i], members[j]))
                            AddPair(members[i], members[j], reason);
            }
        }

        // Exact fingerprint equality — byte-identical audio; no further guards needed.
        AddGroupPairs(
            ownerSongs.Where(s => !string.IsNullOrEmpty(s.Fingerprint)).GroupBy(s => s.Fingerprint!),
            DuplicateMatchReason.ExactFingerprint,
            "fingerprint",
            (_, _) => true);

        // Shared AcoustID track id — strong identifier, but guard against tag drift with a loose
        // duration check (when both known) and the strong-qualifier gate.
        var identifierTolerance = opts.DuplicateDurationToleranceSeconds * 2;
        AddGroupPairs(
            ownerSongs.Where(s => !string.IsNullOrWhiteSpace(s.AcoustIdTrackId)).GroupBy(s => s.AcoustIdTrackId!),
            DuplicateMatchReason.AcoustIdTrack,
            "acoustid",
            (a, b) => QualifiersCompatible(a, b) && DurationsWithin(a, b, identifierTolerance, requireBoth: false));

        // Shared ISRC — candidate only (dirty tags share ISRCs); confirmation still requires audio.
        AddGroupPairs(
            ownerSongs
                .Select(s => (Song: s, Isrc: ProviderIdentity.NormalizeIsrc(s.Isrc)))
                .Where(x => x.Isrc.Length > 0)
                .GroupBy(x => x.Isrc, x => x.Song),
            DuplicateMatchReason.Isrc,
            "isrc",
            (a, b) => QualifiersCompatible(a, b) && DurationsWithin(a, b, identifierTolerance, requireBoth: false));

        // Metadata blocking: normalized primary artist + title, durations required and within
        // tolerance. This is what catches a FLAC and an MP3 of the same recording whose
        // fingerprints differ as strings.
        AddGroupPairs(
            ownerSongs
                .Select(s => (Song: s, Key: MetadataBlockKey(s)))
                .Where(x => x.Key is not null)
                .GroupBy(x => x.Key!, x => x.Song),
            DuplicateMatchReason.Metadata,
            "metadata",
            (a, b) => QualifiersCompatible(a, b)
                      && DurationsWithin(a, b, opts.DuplicateDurationToleranceSeconds, requireBoth: true));

        return candidates;
    }

    /// <summary>
    /// The metadata blocking key: normalized primary artist (a featuring credit folds to its lead)
    /// and normalized title, or null when either is blank and the song can't be blocked on text.
    /// </summary>
    internal static string? MetadataBlockKey(SongMetadata song)
    {
        var artist = ArtistCreditNormalizer.GetPrimaryArtist(song.Artist) ?? song.Artist;
        var artistKey = TitleNormalizer.NormalizeForSearch(artist);
        var titleKey = TitleNormalizer.NormalizeForSearch(song.Title);
        if (artistKey.Length == 0 || titleKey.Length == 0)
            return null;
        return $"{artistKey}\u0001{titleKey}";
    }
}
