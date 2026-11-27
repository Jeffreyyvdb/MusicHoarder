using System.Text.Json.Serialization;

namespace MusicHoarder.Api.BackgroundWork;

/// <summary>The jobs the Background work section reports on. Wire ids; never rename one.</summary>
public static class BackgroundJobIds
{
    public const string Lyrics = "lyrics";
    public const string TranscriptionAllowance = "transcription-allowance";
    public const string CoverArt = "cover-art";
    public const string MusicVideos = "music-videos";
    public const string MusicVideoDownloads = "music-video-downloads";
    public const string QualityGrading = "quality-grading";
    public const string EnrichmentRetries = "enrichment-retries";
    public const string AlbumFillAndFollows = "album-fill-follows";

    public static readonly IReadOnlyList<string> All =
        [Lyrics, TranscriptionAllowance, CoverArt, MusicVideos, MusicVideoDownloads, QualityGrading, EnrichmentRetries, AlbumFillAndFollows];
}

/// <summary>What a job is doing right now, as its service last reported it.</summary>
public static class BackgroundJobState
{
    /// <summary>Turned off by configuration or a runtime setting.</summary>
    public const string Off = "off";

    /// <summary>Nothing to do until its next sweep.</summary>
    public const string Idle = "idle";

    /// <summary>In a pass right now.</summary>
    public const string Working = "working";

    /// <summary>Has work but is held back: an audio allowance, a provider's rate limit, a cooldown.</summary>
    public const string Waiting = "waiting";

    /// <summary>Its last pass threw.</summary>
    public const string Failing = "failing";

    /// <summary>The service has not reported since the API started.</summary>
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = [Off, Idle, Working, Waiting, Failing, Unknown];
}

/// <summary>
/// The meaning of a chart mark or figure, not its colour: the client maps each tone to its palette, so a
/// failure reads as a failure on every chart.
/// </summary>
public static class BackgroundTone
{
    public const string Good = "good";
    public const string Ai = "ai";
    public const string Neutral = "neutral";
    public const string Pending = "pending";
    public const string Warning = "warning";
    public const string Bad = "bad";
    public const string Muted = "muted";
}

public static class BackgroundFormat
{
    public const string Count = "count";

    /// <summary>Seconds, shown as "5h 33m" or "1,200 s" by size.</summary>
    public const string Duration = "duration";

    public const string Bytes = "bytes";

    /// <summary>A fraction from 0 to 1.</summary>
    public const string Percent = "percent";
}

/// <summary><c>GET /api/background-work</c>: every job's row on the Pipeline page.</summary>
public sealed record BackgroundWorkSummaryDto(DateTime GeneratedAtUtc, IReadOnlyList<BackgroundJobSummaryDto> Jobs);

/// <summary>
/// One job's row. Every label is written here so the row and the sheet say the same thing.
/// </summary>
/// <param name="State">One of <see cref="BackgroundJobState"/>.</param>
/// <param name="StateLabel">What the state means right now, e.g. "Waiting for the audio allowance until 03:00".</param>
/// <param name="LastWorkedAtUtc">When the job last did useful work, from its own rows where it writes any.</param>
/// <param name="NextRunAtUtc">When it will next look for work, when it sleeps on a timer.</param>
/// <param name="Headline">The one figure the row leads with.</param>
/// <param name="Trend">Work done per UTC day over the last 14 days, oldest first; exactly 14 values.</param>
/// <param name="TrendLabel">What one unit of the trend is, e.g. "songs synced per day".</param>
public sealed record BackgroundJobSummaryDto(
    string Id,
    string Title,
    string Description,
    string State,
    string StateLabel,
    DateTime? LastWorkedAtUtc,
    DateTime? NextRunAtUtc,
    BackgroundHeadlineDto Headline,
    IReadOnlyList<double> Trend,
    string TrendLabel);

/// <param name="Value">The figure, in <paramref name="Format"/>.</param>
/// <param name="Of">The whole it is part of, when it is a part (draws the row's coverage bar).</param>
/// <param name="Caption">What the figure counts, e.g. "of 21,006 songs have synced lyrics".</param>
/// <param name="EtaUtc">When the backlog runs out at the recent rate; null when there is none or no rate yet.</param>
/// <param name="EtaLabel">How the estimate was made, e.g. "at the last 7 days' rate of 64 songs a day".</param>
public sealed record BackgroundHeadlineDto(
    double Value,
    double? Of,
    string Format,
    string Caption,
    DateTime? EtaUtc,
    string? EtaLabel);

/// <summary><c>GET /api/background-work/{id}</c>: the row plus the charts of its sheet, in reading order.</summary>
public sealed record BackgroundJobDetailDto(BackgroundJobSummaryDto Summary, IReadOnlyList<BackgroundPanelDto> Panels);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(BreakdownPanelDto), "breakdown")]
[JsonDerivedType(typeof(SeriesPanelDto), "series")]
[JsonDerivedType(typeof(LibraryMapPanelDto), "library-map")]
[JsonDerivedType(typeof(BarsPanelDto), "bars")]
[JsonDerivedType(typeof(FiguresPanelDto), "figures")]
public abstract record BackgroundPanelDto(string Title, string? Caption);

/// <summary>
/// Parts of one whole, drawn as a single stacked bar with a legend: where every song's lyrics came from.
/// Segments are in drawing order and sum to <see cref="Total"/>.
/// </summary>
public sealed record BreakdownPanelDto(
    string Title,
    string? Caption,
    long Total,
    string Unit,
    IReadOnlyList<BreakdownSegmentDto> Segments) : BackgroundPanelDto(Title, Caption);

/// <param name="Note">A short explanation shown with the legend entry.</param>
public sealed record BreakdownSegmentDto(string Key, string Label, long Count, string Tone, string? Note);

/// <summary>
/// Values over time in fixed buckets, drawn as columns (stacked when <see cref="Stacked"/>) with
/// horizontal limit lines: audio seconds per hour against the hourly ceilings.
/// </summary>
/// <param name="Bucket">"hour" or "day"; buckets are UTC and contiguous, with zeros filled in.</param>
/// <param name="Format">One of <see cref="BackgroundFormat"/>; applies to values and limits.</param>
public sealed record SeriesPanelDto(
    string Title,
    string? Caption,
    string Bucket,
    string Format,
    bool Stacked,
    IReadOnlyList<SeriesDefinitionDto> Series,
    IReadOnlyList<SeriesPointDto> Points,
    IReadOnlyList<SeriesLimitDto> Limits) : BackgroundPanelDto(Title, Caption);

public sealed record SeriesDefinitionDto(string Key, string Label, string Tone);

/// <param name="Values">One value per entry of the panel's <c>Series</c>, in the same order.</param>
public sealed record SeriesPointDto(DateTime StartUtc, IReadOnlyList<double> Values);

public sealed record SeriesLimitDto(string Label, double Value, string Tone);

/// <summary>
/// Where in the library a sweep has got to. The songs the sweep considers are sorted by when they were added
/// and cut into bins of equal song count, oldest first, so the x axis is the library itself; each bin says how
/// many of its songs are done, still waiting, or outside this sweep. A sweep that works from both ends shows
/// two done stretches closing on the middle.
/// </summary>
/// <param name="Markers">Points to label on the axis, such as how far each end has reached.</param>
public sealed record LibraryMapPanelDto(
    string Title,
    string? Caption,
    string DoneLabel,
    string WaitingLabel,
    string ExcludedLabel,
    IReadOnlyList<LibraryMapBinDto> Bins,
    IReadOnlyList<LibraryMapMarkerDto> Markers) : BackgroundPanelDto(Title, Caption);

public sealed record LibraryMapBinDto(DateTime FromUtc, DateTime ToUtc, int Done, int Waiting, int Excluded);

public sealed record LibraryMapMarkerDto(string Label, DateTime AtUtc);

/// <summary>A distribution over labelled categories, drawn as columns: grade scores, attempts per provider.</summary>
public sealed record BarsPanelDto(
    string Title,
    string? Caption,
    string Format,
    IReadOnlyList<BarDto> Bars) : BackgroundPanelDto(Title, Caption);

public sealed record BarDto(string Label, double Value, string Tone);

/// <summary>A few labelled numbers: backlogs per pass, what is left of today's allowance.</summary>
public sealed record FiguresPanelDto(
    string Title,
    string? Caption,
    IReadOnlyList<FigureDto> Items) : BackgroundPanelDto(Title, Caption);

/// <param name="Value">The number, in <paramref name="Format"/>; null when the figure is a date only.</param>
/// <param name="AtUtc">A moment the figure names, such as when a cooldown ends.</param>
public sealed record FigureDto(string Label, double? Value, string Format, DateTime? AtUtc, string? Sublabel, string Tone);
