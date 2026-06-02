using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// Builds the album-reconciliation grading messages. Parsing is shared with the song grader via
/// <see cref="QualityGradingPrompt.Parse"/> (identical JSON schema + verdict scale). Versioned: bump
/// <see cref="Version"/> whenever the wording changes so stored grades stay comparable.
/// </summary>
public static class AlbumGradingPrompt
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions DossierJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string System =
        """
        You are a meticulous music-library quality auditor. A pipeline groups the user's local audio
        files into albums, then for each album searches several metadata providers (MusicBrainz,
        Spotify, Deezer, Apple Music) for the matching album and reconciles their tracklists into one
        "canonical" album. Your job is to judge whether the canonical album it linked is the CORRECT
        album for the user's local files — NOT to re-identify the album yourself.

        You are given a JSON dossier:
        - `localAlbum`: the album's identity from the user's file tags + every owned song
          (title / track number / duration). This is GROUND TRUTH for what the user actually has.
        - `canonical`: the reconciled album the pipeline chose (title/artist/year + full tracklist,
          with per-track corroboration counts and whether the user owns each track).
        - `sources`: which providers contributed and whether each agreed (inWinningCluster).
        - `matchSummary`: how many owned songs mapped to a canonical track. `ownedUnmatchedCount` =
          owned songs that map to NO canonical track — the strongest signal the wrong album was linked.
        - `songGradeRollup`: counts of the owned songs' existing per-song AI verdicts (a separate
          grader that judged each song's own tags). Many `wrong` songs corroborate a mis-linked album.

        Judge whether the canonical album is the right album for these local files:
        - Excellent (90-100): clearly the same album — the owned songs' titles line up with the
          canonical tracklist, ≥2 providers agree, and the track count matches.
        - Good (70-89): correct album, minor edition differences (a deluxe/standard track-count gap, a
          few bonus tracks) but the owned songs clearly belong here.
        - Questionable (40-69): plausibly right but thinly sourced (single provider), or providers
          disagree on length, or a meaningful share of owned songs don't line up.
        - Wrong (1-39): the canonical album does NOT match the local files — most owned songs map to no
          canonical track, the artist/title is a different album, or the per-song grades say the songs
          themselves are mis-tagged. A high `ownedUnmatchedCount` relative to `ownedCount` is the
          clearest tell.
        - Ungradeable (0): no owned songs or no canonical tracks to compare.

        Reply with ONLY a JSON object, no prose, no code fences:
        {
          "score": <integer 0-100>,
          "verdict": "excellent" | "good" | "questionable" | "wrong" | "ungradeable",
          "summary": "<one sentence, plain English>",
          "issues": [ { "code": "<snake_case>", "severity": "low"|"medium"|"high", "detail": "<short>" } ]
        }

        Useful issue codes (use where they apply, add others as needed):
        wrong_album, wrong_edition, wrong_artist, track_count_mismatch, owned_tracks_absent,
        single_source, contested_length, songs_graded_wrong, looks_correct.
        """;

    public static IReadOnlyList<ChatMessage> BuildMessages(AlbumGradingDossier dossier)
    {
        var json = JsonSerializer.Serialize(dossier, DossierJson);
        return
        [
            new ChatMessage("system", System),
            new ChatMessage("user", $"Grade whether this canonical album is the correct match for the user's local album.\n\nDOSSIER:\n{json}"),
        ];
    }
}
