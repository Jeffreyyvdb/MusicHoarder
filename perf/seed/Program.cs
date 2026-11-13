using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

// Seeds a synthetic but realistically shaped library for the performance suite: rows that look
// like an enriched, built library (fingerprints, lyrics, provider ids, likes, plays, playlists)
// and real audio files behind them, so streaming and covers are measured too.
//
// The files are hard links to a handful of template tracks, so 10,000 songs cost a few megabytes
// of disk. Each row records the exact size and mtime of its file, which is what makes the API's
// startup scan leave the rows alone.

var options = SeedOptions.Parse(args);
if (options is null) return 2;

var rng = new Random(options.Seed);
var dbOptions = new DbContextOptionsBuilder<MusicHoarderDbContext>()
    .UseNpgsql(options.Connection, npgsql => npgsql.CommandTimeout(600))
    .Options;

await using (var db = new MusicHoarderDbContext(dbOptions))
{
    await db.Database.MigrateAsync();

    var existing = await db.Songs.IgnoreQueryFilters()
        .CountAsync(s => s.OwnerUserId == WellKnownUsers.OwnerId && s.DeletedAtUtc == null);
    if (existing >= options.Songs)
    {
        Console.WriteLine($"perf-seed: {existing} songs already present, nothing to do.");
        return 0;
    }
    if (existing > 0)
    {
        Console.Error.WriteLine(
            $"perf-seed: the database holds {existing} songs but {options.Songs} were asked for. " +
            "Seed into an empty database instead of topping one up.");
        return 1;
    }
}

var templates = Directory.GetFiles(options.Templates)
    .Where(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
             || f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f, StringComparer.Ordinal)
    .ToArray();
if (templates.Length == 0)
{
    Console.Error.WriteLine($"perf-seed: no .mp3 or .flac template tracks in {options.Templates}.");
    return 1;
}

// A whole-second mtime in the past: the scanner compares mtimes to the microsecond and skips files
// touched within its settle window.
var templateStamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
foreach (var template in templates)
    File.SetLastWriteTimeUtc(template, templateStamp);
var templateInfo = templates.Select(t => new FileInfo(t)).ToArray();

var now = DateTime.UtcNow;
var songs = new List<SongMetadata>(options.Songs);
var artistCount = Math.Max(8, options.Songs / 28);
var artists = Enumerable.Range(0, artistCount).Select(i => Names.Artist(rng, i)).Distinct().ToArray();

while (songs.Count < options.Songs)
{
    var artist = artists[Zipf(rng, artists.Length)];
    var album = Names.Album(rng);
    var year = 1968 + rng.Next(58);
    var trackTotal = rng.Next(100) < 12 ? 1 : 7 + rng.Next(11);
    var genre = Names.Genres[rng.Next(Names.Genres.Length)];
    var label = Names.Labels[rng.Next(Names.Labels.Length)];
    var releaseId = Uuid(rng);
    var releaseDate = $"{year:0000}-{1 + rng.Next(12):00}-{1 + rng.Next(28):00}";
    var acquired = now.AddDays(-rng.Next(3 * 365)).AddMinutes(-rng.Next(1440));
    var templateIndex = rng.Next(templates.Length);
    var extension = Path.GetExtension(templates[templateIndex]).ToLowerInvariant();

    for (var track = 1; track <= trackTotal && songs.Count < options.Songs; track++)
    {
        var title = Names.Title(rng);
        var guest = rng.Next(100) < 14 ? artists[rng.Next(artists.Length)] : null;
        if (guest == artist) guest = null;
        var displayArtist = guest is null ? artist : $"{artist} feat. {guest}";
        var fileName = $"{track:00} {title}{extension}";
        var unique = songs.Count.ToString("x5");
        var sourcePath = Path.Combine(options.Source, Safe(artist), $"{Safe(album)} [{unique[..3]}]", $"{unique} {fileName}");
        var destinationPath = Path.Combine(options.Destination, Safe(artist), $"{Safe(album)} ({year})", fileName);

        var roll = rng.Next(100);
        var status = roll < 92 ? EnrichmentStatus.Matched : roll < 97 ? EnrichmentStatus.NeedsReview : EnrichmentStatus.Failed;
        var built = status == EnrichmentStatus.Matched;
        var durationMs = 110_000 + rng.Next(260_000);

        var lyricsRoll = rng.Next(100);
        var lines = lyricsRoll < 65 ? Names.LyricLines(rng, 34 + rng.Next(40)) : null;

        Link(templates[templateIndex], sourcePath);
        if (built) Link(templates[templateIndex], destinationPath);

        songs.Add(new SongMetadata
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            SourcePath = sourcePath,
            FileName = Path.GetFileName(sourcePath),
            Extension = extension,
            FileSizeBytes = templateInfo[templateIndex].Length,
            LastModifiedUtc = templateStamp,
            IndexedAtUtc = acquired,
            AcquiredAtUtc = acquired,
            Artist = displayArtist,
            Artists = guest is null ? artist : $"{artist};{guest}",
            ArtistMusicBrainzIds = guest is null ? Uuid(rng) : $"{Uuid(rng)};{Uuid(rng)}",
            AlbumArtist = artist,
            ArtistSort = artist,
            AlbumArtistSort = artist,
            Album = album,
            Title = title,
            Year = year,
            TrackNumber = track,
            TotalTracks = trackTotal,
            DiscNumber = 1,
            TotalDiscs = 1,
            ReleaseTypePrimary = trackTotal == 1 ? "Single" : "Album",
            Genre = genre,
            ReleaseDate = releaseDate,
            OriginalReleaseDate = releaseDate,
            Label = label,
            CatalogNumber = $"{label[..3].ToUpperInvariant()}-{rng.Next(100_000):00000}",
            Upc = string.Concat(Enumerable.Range(0, 12).Select(_ => (char)('0' + rng.Next(10)))),
            Composer = Names.Person(rng),
            Copyright = $"℗ {year} {label}",
            DurationMs = durationMs,
            DurationSeconds = durationMs / 1000,
            Bitrate = extension == ".flac" ? 900 + rng.Next(300) : 320,
            HasCoverArt = true,
            Fingerprint = Fingerprint(rng, durationMs),
            Isrc = $"US{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}{rng.Next(10)}{year % 100:00}{rng.Next(100_000):00000}",
            MusicBrainzId = Uuid(rng),
            MusicBrainzReleaseId = releaseId,
            MusicBrainzReleaseGroupId = Uuid(rng),
            AlbumArtistMusicBrainzId = Uuid(rng),
            SpotifyId = Base62(rng, 22),
            AcoustIdTrackId = Uuid(rng),
            LrclibId = lines is null ? null : rng.Next(1, 9_000_000).ToString(),
            EnrichmentStatus = status,
            MatchedBy = status == EnrichmentStatus.Failed ? null : Names.Providers[rng.Next(Names.Providers.Length)],
            MatchConfidence = status == EnrichmentStatus.Failed ? null : Math.Round(0.55 + rng.NextDouble() * 0.45, 3),
            MatchWarnings = status == EnrichmentStatus.NeedsReview
                ? JsonSerializer.Serialize(new[] { "Duration differs by 9s", "Album title differs from the file's tag" })
                : null,
            EnrichedAtUtc = acquired.AddMinutes(4),
            EnrichmentLastAttemptedAtUtc = acquired.AddMinutes(4),
            EnrichmentError = status == EnrichmentStatus.Failed ? "No provider returned a candidate." : null,
            LastEnrichmentAlgorithmVersion = EnrichmentAlgorithm.CurrentVersion,
            OriginalMetadataCaptured = true,
            OriginalMetadataCapturedAtUtc = acquired.AddMinutes(4),
            OriginalArtist = displayArtist.ToLowerInvariant(),
            OriginalAlbumArtist = artist.ToLowerInvariant(),
            OriginalAlbum = album.ToLowerInvariant(),
            OriginalTitle = title.ToLowerInvariant(),
            OriginalYear = year,
            OriginalTrackNumber = track,
            LibraryBuildStatus = built ? LibraryBuildStatus.Done : LibraryBuildStatus.Pending,
            LibraryBuiltAtUtc = built ? acquired.AddMinutes(9) : null,
            LibraryBuildLastAttemptedAtUtc = built ? acquired.AddMinutes(9) : null,
            DestinationPath = built ? destinationPath : null,
            LastWrittenAtUtc = built ? acquired.AddMinutes(9) : null,
            LikedAtUtc = rng.Next(100) < 9 ? acquired.AddDays(rng.Next(30)) : null,
            PlayCount = rng.Next(100) < 45 ? Zipf(rng, 120) : 0,
            LastPlayedAtUtc = rng.Next(100) < 45 ? now.AddDays(-rng.Next(200)) : null,
            PlainLyrics = lines is null ? null : string.Join('\n', lines),
            SyncedLyrics = lines is null ? null : Names.Synced(lines, durationMs),
            IsInstrumental = lyricsRoll >= 65 && lyricsRoll < 70 ? true : null,
            LyricsStatus = lines is not null ? LyricsStatus.Fetched
                : lyricsRoll < 70 ? LyricsStatus.Instrumental : LyricsStatus.NotFound,
            LyricsLastAttemptedAtUtc = acquired.AddMinutes(5),
            LyricsFetchAttempts = 1,
            LyricsNextRecheckAfterUtc = now.AddYears(5),
            LyricsSyncStatus = lines is null ? LyricsSyncStatus.NotChecked : LyricsSyncStatus.Ok,
            LyricsSyncCheckedAtUtc = lines is null ? null : acquired.AddMinutes(12),
            LyricsSyncConfidence = lines is null ? null : 0.9,
        });
    }
}

Console.WriteLine($"perf-seed: writing {songs.Count} songs ({artists.Length} artists)…");
const int batchSize = 1000;
for (var offset = 0; offset < songs.Count; offset += batchSize)
{
    await using var db = new MusicHoarderDbContext(dbOptions);
    db.ChangeTracker.AutoDetectChangesEnabled = false;
    db.Songs.AddRange(songs.Skip(offset).Take(batchSize));
    await db.SaveChangesAsync();
}

await using (var db = new MusicHoarderDbContext(dbOptions))
{
    var builtIds = songs.Where(s => s.LibraryBuildStatus == LibraryBuildStatus.Done).Select(s => s.Id).ToArray();
    var sizes = new[] { 40, 120, 350, 800 };
    for (var i = 0; i < sizes.Length; i++)
    {
        var playlist = new Playlist
        {
            OwnerUserId = WellKnownUsers.OwnerId,
            Name = $"Perf playlist {i + 1}",
            CreatedAtUtc = now.AddDays(-30 - i),
            UpdatedAtUtc = now.AddDays(-i),
        };
        foreach (var (songId, position) in builtIds.OrderBy(_ => rng.Next()).Take(Math.Min(sizes[i], builtIds.Length)).Select((id, p) => (id, p)))
            playlist.Entries.Add(new PlaylistEntry { SongId = songId, Position = position, AddedAtUtc = now.AddDays(-i) });
        db.Playlists.Add(playlist);
    }
    await db.SaveChangesAsync();
    await db.Database.ExecuteSqlRawAsync("ANALYZE");
}

Console.WriteLine($"perf-seed: done. {songs.Count} songs, {songs.Count(s => s.DestinationPath != null)} built.");
return 0;

// Skewed pick in [0, n): a few artists hold most of the tracks, a few songs most of the plays.
static int Zipf(Random rng, int n) => (int)(n * Math.Pow(rng.NextDouble(), 2.2));

static string Uuid(Random rng)
{
    var bytes = new byte[16];
    rng.NextBytes(bytes);
    return new Guid(bytes).ToString();
}

static string Base62(Random rng, int length)
{
    const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    return string.Create(length, rng, (span, r) =>
    {
        for (var i = 0; i < span.Length; i++) span[i] = alphabet[r.Next(alphabet.Length)];
    });
}

// A Chromaprint fingerprint is about 8 32-bit values per second of the first two minutes, stored
// base64url: high-entropy text that does not compress. Its size is the point.
static string Fingerprint(Random rng, int durationMs)
{
    var seconds = Math.Min(120, durationMs / 1000);
    var bytes = new byte[seconds * 8 * 4 / 2];
    rng.NextBytes(bytes);
    return "AQAD" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static string Safe(string name) => name.Replace('/', '-').Replace(':', '-');

static void Link(string template, string path)
{
    if (File.Exists(path)) return;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (!OperatingSystem.IsWindows() && Native.link(template, path) == 0) return;
    File.Copy(template, path);
    File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(template));
}

internal static partial class Native
{
    [DllImport("libc", SetLastError = true)]
    internal static extern int link(string oldpath, string newpath);
}

internal sealed record SeedOptions(string Connection, int Songs, string Source, string Destination, string Templates, int Seed)
{
    public static SeedOptions? Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
            values[args[i].TrimStart('-')] = args[i + 1];

        string? Get(string key) => values.TryGetValue(key, out var v) ? v : null;
        if (Get("connection") is not { } connection || Get("source") is not { } source
            || Get("destination") is not { } destination || Get("templates") is not { } templates
            || !int.TryParse(Get("songs"), out var songs) || songs <= 0)
        {
            Console.Error.WriteLine(
                "usage: perf-seed --connection <npgsql> --songs <n> --source <dir> --destination <dir> --templates <dir> [--seed <n>]");
            return null;
        }
        return new SeedOptions(connection, songs, source, destination, templates,
            int.TryParse(Get("seed"), out var seed) ? seed : 20_260_101);
    }
}

internal static class Names
{
    public static readonly string[] Genres =
        ["Hip-Hop", "Rock", "Pop", "Electronic", "Jazz", "R&B", "Indie", "Metal", "Folk", "House", "Soul", "Ambient", "Punk", "Classical"];
    public static readonly string[] Labels =
        ["Northlight Records", "Paper Crane", "Low Tide Audio", "Signal Hill", "Marble Arcade", "Odd Hours", "Greyfield", "Tin Roof Music"];
    public static readonly string[] Providers = ["AcoustId", "SpotifyApi", "MusicBrainzWeb", "Deezer", "AppleMusic"];

    private static readonly string[] Words =
        ["Glass", "River", "Night", "Paper", "Summer", "Ghost", "Silver", "Velvet", "Hollow", "Golden", "Neon", "Quiet",
         "Electric", "Winter", "Cinder", "Marble", "Static", "Honey", "Lantern", "Thunder", "Violet", "Copper", "Feather",
         "Midnight", "Harbor", "Ember", "Signal", "Orchard", "Mirror", "Wild", "Slow", "Distant", "Broken", "Bright",
         "Crooked", "Salt", "Iron", "Cloud", "Arrow", "Garden", "Motel", "Radio", "Satellite", "Parade", "Fever", "Coast"];
    private static readonly string[] Nouns =
        ["Hearts", "Wolves", "Machines", "Sisters", "Kids", "Club", "Choir", "Parade", "Collective", "Tigers", "Engines",
         "Company", "Brothers", "Society", "Arcade", "Union", "Echoes", "Daughters", "Orchestra", "Riders"];
    private static readonly string[] First = ["Ada", "Milo", "June", "Otis", "Nora", "Felix", "Ines", "Rufus", "Lena", "Theo", "Maya", "Jonas"];
    private static readonly string[] Last = ["Vance", "Okafor", "Lindqvist", "Marlowe", "Castell", "Brandt", "Achebe", "Sorensen", "Halloway", "Reyes"];

    public static string Person(Random rng) => $"{First[rng.Next(First.Length)]} {Last[rng.Next(Last.Length)]}";

    public static string Artist(Random rng, int index) => (index % 3) switch
    {
        0 => $"The {Words[rng.Next(Words.Length)]} {Nouns[rng.Next(Nouns.Length)]}",
        1 => $"{Person(rng)} {index}",
        _ => $"{Words[rng.Next(Words.Length)]} {Words[rng.Next(Words.Length)]} {index}",
    };

    public static string Album(Random rng) => rng.Next(3) switch
    {
        0 => $"{Words[rng.Next(Words.Length)]} {Words[rng.Next(Words.Length)]}",
        1 => $"The {Words[rng.Next(Words.Length)]} {Nouns[rng.Next(Nouns.Length)]} Sessions",
        _ => $"{Words[rng.Next(Words.Length)]} in the {Words[rng.Next(Words.Length)]}",
    };

    public static string Title(Random rng) => rng.Next(4) switch
    {
        0 => Words[rng.Next(Words.Length)],
        1 => $"{Words[rng.Next(Words.Length)]} {Words[rng.Next(Words.Length)]}",
        2 => $"{Words[rng.Next(Words.Length)]} {Nouns[rng.Next(Nouns.Length)]} (Interlude)",
        _ => $"{Words[rng.Next(Words.Length)]} of the {Words[rng.Next(Words.Length)]}",
    };

    public static string[] LyricLines(Random rng, int count)
    {
        var lines = new string[count];
        for (var i = 0; i < count; i++)
        {
            var length = 4 + rng.Next(6);
            lines[i] = string.Join(' ', Enumerable.Range(0, length).Select(_ => Words[rng.Next(Words.Length)].ToLowerInvariant()));
        }
        return lines;
    }

    public static string Synced(string[] lines, int durationMs)
    {
        var builder = new StringBuilder(lines.Length * 48);
        var step = (durationMs - 12_000) / lines.Length;
        for (var i = 0; i < lines.Length; i++)
        {
            var at = 8_000 + i * step;
            builder.Append('[').Append((at / 60_000).ToString("00")).Append(':')
                .Append((at % 60_000 / 1000.0).ToString("00.00", System.Globalization.CultureInfo.InvariantCulture))
                .Append("] ").Append(lines[i]).Append('\n');
        }
        return builder.ToString();
    }
}
