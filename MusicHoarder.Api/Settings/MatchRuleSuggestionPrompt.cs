using System.Text.Json;
using System.Text.Json.Serialization;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Settings;

/// <summary>A sampled unmatched song the suggester analyzes.</summary>
public sealed record SongSample(string? Title, string? Artist, string? FileName);

/// <summary>A rule the suggester proposes (before validation/scoring); mirrors <see cref="MatchRuleInput"/>.</summary>
public sealed record RawSuggestion(
    string Name, string Pattern, string SourceField, string? AlbumOverride, string? AlbumArtistOverride);

/// <summary>
/// Builds the LLM prompt that turns a sample of messy, unmatched song titles/filenames into proposed
/// <see cref="MetadataMatchRule"/>s, and parses the JSON reply. Mirrors the versioned
/// build/parse pattern of <c>QualityGradingPrompt</c>.
/// </summary>
public static class MatchRuleSuggestionPrompt
{
    public const int Version = 1;

    /// <summary>Cap the sample sent to the model to keep the prompt small.</summary>
    public const int MaxSamples = 120;

    private const string System = """
        You configure metadata "match rules" for a self-hosted music library. The user has many files
        downloaded from YouTube (e.g. Dutch rap "sessions") that the music databases don't recognize, so
        they sit unmatched. Your job: look at a sample of their messy titles/filenames, spot the recurring
        series, and propose rules that recognize and re-tag them.

        A rule has:
        - "pattern": a template of literal text plus {artist} / {title} / {album} / {albumartist}
          placeholders, e.g. "{artist} | {title} | 101Barz". Literal text (like a channel/series name)
          anchors the match; placeholders capture the variable parts. Separators are matched flexibly
          (a "|" in the template also matches "-", "_", or a fullwidth "｜"), and leading track numbers
          and trailing "[youtubeid]" / "(1)" are ignored automatically — do NOT put those in the pattern.
        - "sourceField": "title" (default) or "filename" — which the pattern is matched against.
        - "albumOverride" / "albumArtistOverride": optional CONSTANT values. Use these to group a whole
          series as one compilation — e.g. set albumArtistOverride to the channel ("101Barz") and
          albumOverride to a series album ("101Barz sessies") so every track, by any artist, lands in one
          album. Omit (null) when not grouping.
        - "name": a short human label, e.g. "101Barz sessions".

        Guidance:
        - Propose 1-4 rules covering the most common recurring patterns you actually see in the sample.
        - Prefer one tolerant rule per series over many near-duplicates.
        - For a series where artists vary but the channel is constant, set albumArtistOverride to the
          channel and albumOverride to a sensible series album so they group together.
        - It is fine to use a {title}-only pattern with overrides to at least group structureless names.

        Reply with ONLY a JSON object, no prose, no code fences:
        {"rules":[{"name":"...","pattern":"...","sourceField":"title","albumOverride":null,"albumArtistOverride":null}]}
        """;

    private static readonly JsonSerializerOptions SampleJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<ChatMessage> BuildMessages(IReadOnlyList<SongSample> samples)
    {
        var capped = samples.Count > MaxSamples ? samples.Take(MaxSamples).ToList() : samples;
        var json = JsonSerializer.Serialize(capped, SampleJson);
        return
        [
            new ChatMessage("system", System),
            new ChatMessage("user", $"Unmatched songs (title / artist / fileName):\n{json}"),
        ];
    }

    /// <summary>Lenient parse of the model's reply; returns an empty list on malformed output.</summary>
    public static IReadOnlyList<RawSuggestion> Parse(string content)
    {
        var json = ExtractJsonObject(content);
        var result = new List<RawSuggestion>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in rules.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var pattern = Str(item, "pattern");
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;
                var name = Str(item, "name") ?? "Suggested rule";
                var sourceField = Str(item, "sourceField") ?? "title";
                result.Add(new RawSuggestion(name!, pattern!, sourceField, Str(item, "albumOverride"), Str(item, "albumArtistOverride")));
            }
        }
        catch (JsonException)
        {
            // Malformed reply — caller falls back to the heuristic suggester.
        }

        return result;
    }

    private static string? Str(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string ExtractJsonObject(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }
}
