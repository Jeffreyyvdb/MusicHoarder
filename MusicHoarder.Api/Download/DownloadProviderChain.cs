using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Download;

/// <summary>
/// Resolves the configured, ordered download-provider chain (<c>MusicEnricher:DownloadProviders</c>,
/// falling back to the legacy single <c>DownloadProvider</c>) to concrete provider instances by name.
/// Shared by the wishlist downloader (over <see cref="IDownloadProvider"/>) and the quality-upgrade
/// service (over <see cref="IUpgradeProvider"/>) so both honour one order with one implementation.
/// </summary>
public static class DownloadProviderChain
{
    /// <summary>The ordered provider names: <see cref="MusicEnricherOptions.DownloadProviders"/> when set,
    /// else the single legacy <see cref="MusicEnricherOptions.DownloadProvider"/>.</summary>
    public static IReadOnlyList<string> Names(MusicEnricherOptions opts) =>
        opts.DownloadProviders is { Length: > 0 } ? opts.DownloadProviders : [opts.DownloadProvider];

    /// <summary>
    /// Maps <paramref name="names"/> to matching providers in order, skipping blank slots (optional
    /// trailing compose entries) and de-duplicating. Unknown names are logged and skipped. The result
    /// may be empty — callers decide what an empty chain means (the wishlist downloader falls back to
    /// its first registered provider; the upgrade chain simply no-ops).
    /// </summary>
    public static List<T> Resolve<T>(
        IEnumerable<string> names, IEnumerable<T> providers, Func<T, string> nameOf, ILogger logger)
    {
        var available = providers.ToList();
        var resolved = new List<T>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var provider = available.FirstOrDefault(
                p => string.Equals(nameOf(p), name, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
                logger.LogWarning("Unknown download provider '{Name}' in chain — skipping", name);
            else if (!resolved.Contains(provider))
                resolved.Add(provider);
        }
        return resolved;
    }
}
