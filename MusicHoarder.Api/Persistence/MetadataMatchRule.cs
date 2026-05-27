using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

/// <summary>Which song field a <see cref="MetadataMatchRule"/> matches its pattern against.</summary>
public enum MatchRuleSourceField
{
    /// <summary>The resolved title (embedded tag, falling back to the file path).</summary>
    Title = 0,

    /// <summary>The file name without its extension.</summary>
    FileName = 1,
}

/// <summary>
/// A user-defined rule that recognizes a song by a template pattern and rewrites its metadata from
/// the captured fields — for content the mainstream catalogs don't cover (e.g. YouTube channel
/// uploads like "Yung Nnelg | Wintersessie 2020 | 101Barz"). The pattern is a template with
/// <c>{artist}</c>/<c>{title}</c>/<c>{album}</c>/<c>{albumartist}</c> placeholders and literal text;
/// literals anchor the match while placeholders capture into the corresponding fields. Managed at
/// runtime from the Settings UI and applied by the custom-rule enrichment provider.
/// </summary>
public class MetadataMatchRule
{
    [Key]
    public int Id { get; set; }

    /// <summary>Human-readable label, e.g. "101Barz sessions".</summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Disabled rules are kept but skipped by the provider.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lower runs first; the first rule that matches a song wins.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>The template pattern, e.g. <c>{artist} | {title} | 101Barz</c>.</summary>
    [MaxLength(1000)]
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Which song field the pattern is matched against.</summary>
    public MatchRuleSourceField SourceField { get; set; } = MatchRuleSourceField.Title;

    /// <summary>
    /// Optional constant album to assign on match (not captured from the pattern). Lets many songs
    /// with different track artists collapse into one album, e.g. "101Barz sessies". Null = leave the
    /// album to a captured <c>{album}</c> placeholder or the song's existing value.
    /// </summary>
    [MaxLength(200)]
    public string? AlbumOverride { get; set; }

    /// <summary>
    /// Optional constant album artist to assign on match, e.g. "101Barz". Combined with
    /// <see cref="AlbumOverride"/> this groups a compilation under one album artist while each track
    /// keeps its own (captured) artist. Null = derive from the captured/track artist as before.
    /// </summary>
    [MaxLength(200)]
    public string? AlbumArtistOverride { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
