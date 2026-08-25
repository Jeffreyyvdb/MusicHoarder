using System.Text.Json;

namespace MusicHoarder.Api.Enrichment.AlbumTracklist;

/// <summary>
/// Decodes <see cref="Persistence.CanonicalAlbum.SourcesJson"/> back into the
/// <see cref="AlbumTracklistReconciler.ReconciledSource"/> entries that
/// <see cref="CanonicalAlbumFetchService"/> serialized into it — the one place that knows the
/// stored schema, so readers can never drift from the writer. Empty or malformed payloads read as
/// "no sources".
/// </summary>
public static class CanonicalAlbumSources
{
    public static string Serialize(IReadOnlyList<AlbumTracklistReconciler.ReconciledSource> sources) =>
        JsonSerializer.Serialize(sources);

    public static IReadOnlyList<AlbumTracklistReconciler.ReconciledSource> Parse(string? sourcesJson)
    {
        if (string.IsNullOrWhiteSpace(sourcesJson))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<AlbumTracklistReconciler.ReconciledSource>>(sourcesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Distinct provider names that won the reconciliation cluster (for the UI badge/chip).</summary>
    public static string[] WinningProviderNames(string? sourcesJson) =>
        Parse(sourcesJson)
            .Where(s => s.InWinningCluster)
            .Select(s => s.Provider.ToString())
            .Distinct()
            .ToArray();
}
