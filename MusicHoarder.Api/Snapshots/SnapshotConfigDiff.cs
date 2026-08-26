using System.Text.Json;

namespace MusicHoarder.Api.Snapshots;

/// <summary>One pipeline setting that moved between two snapshots.</summary>
public readonly record struct ConfigChange(string Key, string? From, string? To);

/// <summary>
/// Compares the behavioural fingerprints two <see cref="Persistence.EnrichmentSnapshot"/>s captured —
/// enabled providers, consensus thresholds, the AI model and prompt version. Shared by the snapshot
/// compare endpoint (which shows the full diff) and the History feed (which turns it into the sentence
/// "why did my match rate move overnight").
/// </summary>
public static class SnapshotConfigDiff
{
    public static IReadOnlyList<ConfigChange> Diff(string fromJson, string toJson)
    {
        var fromFlat = new Dictionary<string, string?>();
        var toFlat = new Dictionary<string, string?>();
        try { Flatten(JsonSerializer.Deserialize<JsonElement>(fromJson), "", fromFlat); } catch (JsonException) { }
        try { Flatten(JsonSerializer.Deserialize<JsonElement>(toJson), "", toFlat); } catch (JsonException) { }

        var changes = new List<ConfigChange>();
        foreach (var key in fromFlat.Keys.Union(toFlat.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            fromFlat.TryGetValue(key, out var from);
            toFlat.TryGetValue(key, out var to);
            if (from != to) changes.Add(new ConfigChange(key, from, to));
        }
        return changes;
    }

    /// <summary>Flattens nested JSON to dotted keys so two fingerprints compare key by key.</summary>
    private static void Flatten(JsonElement el, string prefix, Dictionary<string, string?> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}", into);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    Flatten(item, $"{prefix}[{i++}]", into);
                break;
            default:
                into[prefix] = el.ToString();
                break;
        }
    }
}
