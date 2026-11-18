using System.Text.Json.Serialization;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Jobs;

public enum RecentActivityKind { Added, Matched, Review, Failed }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SongSubject), "song")]
[JsonDerivedType(typeof(AlbumSubject), "album")]
public abstract record RecentActivitySubject;

public sealed record SongSubject(int SongId, string Title, string? Artist) : RecentActivitySubject;

/// <summary>Several songs of one album that arrived within minutes of each other. Always two or more.</summary>
public sealed record AlbumSubject : RecentActivitySubject
{
    public AlbumSubject(string album, string? artist, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);
        Album = album;
        Artist = artist;
        Count = count;
    }

    public string Album { get; }
    public string? Artist { get; }
    public int Count { get; }
}

public sealed record RecentActivityEvent(string Id, RecentActivityKind Kind, RecentActivitySubject Subject, DateTime At);

/// <summary>The per-song facts the derivation reads, projected straight from the query.</summary>
public sealed record RecentActivityFacts(
    int Id, string? Title, string? Artist, string? AlbumArtist, string? Album, string FileName,
    DateTime? AcquiredAtUtc, DateTime IndexedAtUtc, DateTime? EnrichedAtUtc, DateTime? EnrichmentLastAttemptedAtUtc,
    DateTime? LibraryBuiltAtUtc, DateTime? LibraryBuildLastAttemptedAtUtc,
    EnrichmentStatus EnrichmentStatus, LibraryBuildStatus LibraryBuildStatus,
    bool InLibrary,
    bool WasEverBuilt);

/// <summary>
/// What changed for the user, as a feed: a song arriving in the library, a match on its way in,
/// something waiting on a person, something that failed. Re-tags, rebuilds, upgrades and re-matches
/// of songs the user already has are background housekeeping and produce no event.
/// </summary>
public static class RecentActivity
{
    // An album downloads or copies in within minutes, and per-key chaining keeps two albums landing at once apart.
    private static readonly TimeSpan BurstGap = TimeSpan.FromMinutes(30);

    private readonly record struct Classified(RecentActivityFacts Song, RecentActivityKind Kind, DateTime At);

    public static IReadOnlyList<RecentActivityEvent> Derive(IEnumerable<RecentActivityFacts> songs, int take)
    {
        var groups = new Dictionary<(RecentActivityKind, string, string), List<Classified>>();
        var events = new List<RecentActivityEvent>();

        foreach (var song in songs)
        {
            if (Classify(song) is not { } c) continue;

            if (string.IsNullOrWhiteSpace(song.Album))
            {
                events.Add(SongEvent(c));
                continue;
            }

            var key = (c.Kind, Fold(song.Album), Fold(song.AlbumArtist ?? song.Artist));
            if (!groups.TryGetValue(key, out var members))
                groups[key] = members = [];
            members.Add(c);
        }

        foreach (var members in groups.Values)
            events.AddRange(Bursts(members));

        return events
            .OrderByDescending(e => e.At)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    private static Classified? Classify(RecentActivityFacts s)
    {
        if (s.EnrichmentStatus == EnrichmentStatus.Failed)
            return new(s, RecentActivityKind.Failed, s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.IndexedAtUtc);
        if (s.LibraryBuildStatus == LibraryBuildStatus.Failed)
            return new(s, RecentActivityKind.Failed, s.LibraryBuildLastAttemptedAtUtc ?? s.EnrichedAtUtc ?? s.IndexedAtUtc);
        if (s.EnrichmentStatus == EnrichmentStatus.NeedsReview)
            return new(s, RecentActivityKind.Review, s.EnrichedAtUtc ?? s.EnrichmentLastAttemptedAtUtc ?? s.IndexedAtUtc);
        if (s.InLibrary)
            return new(s, RecentActivityKind.Added, s.AcquiredAtUtc ?? OldestOf(s.LibraryBuiltAtUtc, s.IndexedAtUtc));
        if (s.EnrichmentStatus == EnrichmentStatus.Matched && !s.WasEverBuilt && s.EnrichedAtUtc is { } enrichedAt)
            return new(s, RecentActivityKind.Matched, enrichedAt);
        return null;
    }

    private static IEnumerable<RecentActivityEvent> Bursts(List<Classified> members)
    {
        var ordered = members.OrderBy(m => m.At).ThenBy(m => m.Song.Id).ToList();
        var burst = new List<Classified> { ordered[0] };

        foreach (var next in ordered.Skip(1))
        {
            if (next.At - burst[^1].At > BurstGap)
            {
                yield return ToEvent(burst);
                burst = [];
            }
            burst.Add(next);
        }
        yield return ToEvent(burst);
    }

    private static RecentActivityEvent ToEvent(List<Classified> burst)
    {
        if (burst.Count == 1) return SongEvent(burst[0]);

        var newest = burst[^1];
        var oldest = burst[0];
        return new RecentActivityEvent(
            $"{KindId(newest.Kind)}-album-{oldest.Song.Id}",
            newest.Kind,
            new AlbumSubject(newest.Song.Album!, newest.Song.AlbumArtist ?? newest.Song.Artist, burst.Count),
            newest.At);
    }

    private static RecentActivityEvent SongEvent(Classified c)
    {
        var title = string.IsNullOrWhiteSpace(c.Song.Title) ? c.Song.FileName : c.Song.Title;
        return new RecentActivityEvent(
            $"{KindId(c.Kind)}-{c.Song.Id}",
            c.Kind,
            new SongSubject(c.Song.Id, title, c.Song.Artist ?? c.Song.AlbumArtist),
            c.At);
    }

    private static string KindId(RecentActivityKind kind) => kind.ToString().ToLowerInvariant();

    private static string Fold(string? text) => (text ?? string.Empty).ToLowerInvariant().Trim();

    private static DateTime OldestOf(DateTime? a, DateTime b) => a is { } value && value < b ? value : b;

    /// <summary>The overview's wire shape: the event plus its relative time.</summary>
    public static object ToWire(RecentActivityEvent e, DateTime now) => new
    {
        e.Id,
        Kind = KindId(e.Kind),
        e.At,
        Time = TimeAgo(e.At, now),
        e.Subject,
    };

    public static string TimeAgo(DateTime at, DateTime now)
    {
        var diff = now - at;
        return diff.TotalMinutes < 1 ? "just now"
            : diff.TotalMinutes < 60 ? $"{(int)diff.TotalMinutes} min ago"
            : diff.TotalHours < 24 ? $"{(int)diff.TotalHours} hr ago"
            : $"{(int)diff.TotalDays} day{(diff.TotalDays >= 2 ? "s" : "")} ago";
    }
}
