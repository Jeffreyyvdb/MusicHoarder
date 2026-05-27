using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;

namespace MusicHoarder.Api.Settings;

/// <summary>A proposed rule plus how well it fits the current unmatched library.</summary>
public sealed record MatchRuleSuggestion(
    string Name, string Pattern, string SourceField,
    string? AlbumOverride, string? AlbumArtistOverride,
    int MatchCount, int SampleSize, IReadOnlyList<string> Examples);

public sealed record MatchRuleSuggestionResult(
    bool Configured, string Source, int SampleSize, IReadOnlyList<MatchRuleSuggestion> Suggestions);

/// <summary>
/// Proposes match-rule presets by analyzing the currently-unmatched songs — Paperless-ngx style. Uses
/// the LLM (<see cref="IChatCompletionClient"/>, shared with the quality grader) when configured,
/// otherwise a deterministic shared-anchor heuristic. Every proposal is validated and scored against the
/// real sample (compiled + match-counted) so the UI can show "matches N of M unmatched". Never saves.
/// </summary>
public interface IMatchRuleSuggestionService
{
    Task<MatchRuleSuggestionResult> SuggestAsync(CancellationToken ct = default);
}

public sealed class MatchRuleSuggestionService(
    IServiceScopeFactory scopeFactory,
    IChatCompletionClient chatClient,
    IOptionsMonitor<QualityGradingOptions> options,
    ILogger<MatchRuleSuggestionService> logger) : IMatchRuleSuggestionService
{
    private const int SampleLimit = 200;
    private const int MaxSuggestions = 6;
    private const int MaxExamples = 3;

    public async Task<MatchRuleSuggestionResult> SuggestAsync(CancellationToken ct = default)
    {
        var samples = await LoadUnmatchedSampleAsync(ct);
        if (samples.Count == 0)
            return new MatchRuleSuggestionResult(chatClient.IsConfigured, "none", 0, []);

        IReadOnlyList<RawSuggestion> raw = [];
        var source = "heuristic";

        if (chatClient.IsConfigured)
        {
            try
            {
                var opts = options.CurrentValue;
                var result = await chatClient.CompleteAsync(
                    new ChatCompletionRequest(MatchRuleSuggestionPrompt.BuildMessages(samples), opts.Temperature, opts.MaxOutputTokens),
                    ct);
                raw = MatchRuleSuggestionPrompt.Parse(result.Content);
                source = "llm";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Match-rule LLM suggestion failed; falling back to heuristic.");
            }
        }

        if (raw.Count == 0)
        {
            raw = HeuristicSuggestions(samples);
            source = "heuristic";
        }

        var scored = ScoreAndFilter(raw, samples);
        return new MatchRuleSuggestionResult(chatClient.IsConfigured, source, samples.Count, scored);
    }

    private async Task<List<SongSample>> LoadUnmatchedSampleAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        return await db.Songs
            .IgnoreQueryFilters()
            .Where(s => s.DeletedAtUtc == null && s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            .OrderBy(s => s.Id)
            .Take(SampleLimit)
            .Select(s => new SongSample(s.Title, s.Artist, s.FileName))
            .ToListAsync(ct);
    }

    /// <summary>Compiles, match-counts, and orders proposals; drops invalid/zero-match ones.</summary>
    private static IReadOnlyList<MatchRuleSuggestion> ScoreAndFilter(IReadOnlyList<RawSuggestion> raw, IReadOnlyList<SongSample> samples)
    {
        var scored = new List<MatchRuleSuggestion>();
        var seenPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in raw)
        {
            if (!seenPatterns.Add($"{r.SourceField}|{r.Pattern}"))
                continue;
            if (!MatchRulePattern.TryCompile(r.Pattern, out var compiled, out _))
                continue;

            var field = ParseSourceField(r.SourceField);
            var count = 0;
            var examples = new List<string>();
            foreach (var sample in samples)
            {
                var input = InputFor(sample, field);
                if (string.IsNullOrWhiteSpace(input))
                    continue;
                if (MatchRulePattern.Match(compiled!, input) is { } m && m.HasAny)
                {
                    count++;
                    if (examples.Count < MaxExamples)
                        examples.Add(input!);
                }
            }

            if (count == 0)
                continue;

            scored.Add(new MatchRuleSuggestion(
                Name: string.IsNullOrWhiteSpace(r.Name) ? "Suggested rule" : r.Name.Trim(),
                Pattern: r.Pattern.Trim(),
                SourceField: field == MatchRuleSourceField.FileName ? "filename" : "title",
                AlbumOverride: TrimToNull(r.AlbumOverride),
                AlbumArtistOverride: TrimToNull(r.AlbumArtistOverride),
                MatchCount: count,
                SampleSize: samples.Count,
                Examples: examples));
        }

        return scored.OrderByDescending(s => s.MatchCount).Take(MaxSuggestions).ToList();
    }

    /// <summary>
    /// Deterministic fallback: find the most frequent shared token across the sample (e.g. a channel
    /// name like "101Barz"), then propose a tolerant template anchored on it that groups the series
    /// under that album artist. Separator tolerance means a single "|" template covers all variants.
    /// </summary>
    private static IReadOnlyList<RawSuggestion> HeuristicSuggestions(IReadOnlyList<SongSample> samples)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var original = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in Tokenize($"{sample.Title} {Path.GetFileNameWithoutExtension(sample.FileName ?? string.Empty)}"))
            {
                if (!seen.Add(token))
                    continue;
                counts[token] = counts.GetValueOrDefault(token) + 1;
                original.TryAdd(token, token);
            }
        }

        var anchor = counts
            .Where(kv => kv.Value >= 3 && kv.Value >= samples.Count / 4)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => original[kv.Key])
            .FirstOrDefault();

        if (anchor is null)
            return [];

        return
        [
            new RawSuggestion($"{anchor} sessions", $"{{artist}} | {{title}} | {anchor}", "title", null, anchor),
            new RawSuggestion($"{anchor} (loose)", $"{{artist}} | {{title}}", "title", null, anchor),
        ];
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        foreach (var raw in value.Split([' ', '\t', '-', '_', '|', '/', '｜', '·', '–', '—', '.', ',', '[', ']', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            // Keep word-ish tokens with a letter (so "101Barz" stays but bare years/numbers drop out).
            if (token.Length >= 3 && token.Any(char.IsLetter))
                yield return token;
        }
    }

    private static string? InputFor(SongSample sample, MatchRuleSourceField field) => field switch
    {
        MatchRuleSourceField.FileName => Path.GetFileNameWithoutExtension(sample.FileName ?? string.Empty),
        _ => sample.Title,
    };

    private static MatchRuleSourceField ParseSourceField(string? value) =>
        string.Equals(value?.Trim(), "filename", StringComparison.OrdinalIgnoreCase)
            ? MatchRuleSourceField.FileName
            : MatchRuleSourceField.Title;

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
