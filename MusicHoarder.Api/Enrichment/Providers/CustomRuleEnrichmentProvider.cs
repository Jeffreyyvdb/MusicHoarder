using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Enrichment.Providers;

/// <summary>
/// Applies user-defined <see cref="MetadataMatchRule"/>s: when a song's title (or file name) matches a
/// rule's template, the captured fields rewrite the song's metadata. This covers content the mainstream
/// catalogs don't carry (e.g. YouTube channel uploads like "Yung Nnelg | Wintersessie 2020 | 101Barz").
/// A match is <b>authoritative</b> — the user wrote the rule precisely to rewrite these tags — so it
/// recommends <see cref="EnrichmentStatus.Matched"/> at full confidence and the consensus evaluator lets
/// it win outright. Originals are snapshotted before the overwrite, so it stays reversible.
/// </summary>
public sealed class CustomRuleEnrichmentProvider(
    IMatchRuleService rules,
    IOptions<MusicEnricherOptions> options,
    ILogger<CustomRuleEnrichmentProvider> logger) : IEnrichmentProvider
{
    public string Name => "CustomRule";
    public int Priority => 120;

    public bool CanHandle(SongMetadata song) => rules.HasEnabledRules;

    public async Task<ProviderOutcome> TryEnrichAsync(SongMetadata song, CancellationToken ct = default)
    {
        var enabled = await rules.GetEnabledAsync(ct);
        if (enabled.Count == 0)
            return new ProviderNoMatch();

        foreach (var rule in enabled)
        {
            var input = SourceValue(song, rule.SourceField);
            if (string.IsNullOrWhiteSpace(input))
                continue;

            var extraction = MatchRulePattern.Match(rule.Compiled, input);
            if (extraction is null || !extraction.HasAny)
                continue;

            logger.LogInformation(
                "CustomRule '{Rule}' matched {Field}='{Input}' for SongId={SongId} → artist='{Artist}', title='{Title}'",
                rule.Name, rule.SourceField, input, song.Id, extraction.Artist, extraction.Title);

            return new ProviderMatched(BuildResult(song, rule, extraction));
        }

        return new ProviderNoMatch();
    }

    private string SourceValue(SongMetadata song, MatchRuleSourceField field) => field switch
    {
        MatchRuleSourceField.FileName => Path.GetFileNameWithoutExtension(song.FileName),
        // Title (default): the resolved title — embedded tag, falling back to the file path.
        _ => SongSearchText.Resolve(song, options.Value.SourceDirectory).Title ?? string.Empty,
    };

    private static EnrichmentProviderResult BuildResult(SongMetadata song, EnabledMatchRule rule, MatchRuleExtraction extraction)
    {
        // Captured fields win; fall back to the song's existing values for fields the rule didn't set.
        var artist = extraction.Artist ?? song.Artist;
        var title = extraction.Title ?? song.Title;

        // A rule's constant overrides (e.g. a compilation album + album artist) take precedence over
        // both the captured placeholder and the song's existing value, so many tracks with different
        // artists collapse into one album attributed to a single album artist.
        var album = rule.AlbumOverride ?? extraction.Album ?? song.Album;
        var albumArtist = rule.AlbumArtistOverride
            ?? extraction.AlbumArtist
            ?? ArtistCreditNormalizer.GetPrimaryArtist(artist)
            ?? artist;

        return new EnrichmentProviderResult(
            Artist: artist,
            AlbumArtist: albumArtist,
            Title: title,
            Year: null,
            TrackNumber: null,
            MusicBrainzId: null,
            MusicBrainzReleaseId: null,
            SpotifyId: null,
            AcoustIdTrackId: null,
            Isrc: null,
            MatchedBy: "CustomRule",
            MatchConfidence: 1.0,
            MatchWarnings: [$"rule:{rule.Name}"],
            RecommendedStatus: EnrichmentStatus.Matched,
            Album: album,
            Authoritative: true);
    }
}
