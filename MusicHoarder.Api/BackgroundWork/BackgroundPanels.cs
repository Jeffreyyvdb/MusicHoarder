namespace MusicHoarder.Api.BackgroundWork;

/// <summary>Builders for the panels every insight draws, so a rule such as "empty segments are left out" is one rule.</summary>
public static class BackgroundPanels
{
    public sealed record Segment(string Key, string Label, long Count, string Tone, string? Note = null);

    /// <summary>A breakdown whose total is the sum of its parts. Segments with no songs are left out of the bar and legend.</summary>
    public static BreakdownPanelDto Breakdown(string title, string? caption, string unit, IEnumerable<Segment> segments)
    {
        var shown = segments.Where(s => s.Count > 0).ToList();
        return new BreakdownPanelDto(
            title, caption, shown.Sum(s => s.Count), unit,
            [.. shown.Select(s => new BreakdownSegmentDto(s.Key, s.Label, s.Count, s.Tone, s.Note))]);
    }

    public static FigureDto Count(string label, double value, string? sublabel = null, string tone = BackgroundTone.Neutral)
        => new(label, value, BackgroundFormat.Count, null, sublabel, tone);

    public static FigureDto Duration(string label, double seconds, string? sublabel = null, string tone = BackgroundTone.Neutral)
        => new(label, seconds, BackgroundFormat.Duration, null, sublabel, tone);

    /// <param name="bytes">Null when it cannot be read; the figure then shows a dash.</param>
    public static FigureDto Bytes(string label, double? bytes, string? sublabel = null, string tone = BackgroundTone.Neutral)
        => new(label, bytes, BackgroundFormat.Bytes, null, sublabel, tone);

    public static FigureDto Moment(string label, DateTime? atUtc, string? sublabel = null, string tone = BackgroundTone.Neutral)
        => new(label, null, BackgroundFormat.Count, atUtc, sublabel, tone);
}
