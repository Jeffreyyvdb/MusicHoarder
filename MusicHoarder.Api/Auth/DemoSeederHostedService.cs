using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// Runs once on startup after migrations. Responsibilities:
/// <list type="bullet">
///   <item>Update the Owner + Demo user rows' email fields from <see cref="AuthOptions.OwnerEmail"/>
///   / <see cref="AuthOptions.DemoUserEmail"/>. Lets you change your email by re-deploying.</item>
///   <item>Seed ~20 synthetic songs for the demo user on first boot (idempotent — skipped on
///   subsequent boots if any demo song already exists).</item>
/// </list>
/// </summary>
public sealed class DemoSeederHostedService : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AuthOptions> _authOptions;
    private readonly ILogger<DemoSeederHostedService> _logger;

    public DemoSeederHostedService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AuthOptions> authOptions,
        ILogger<DemoSeederHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _authOptions = authOptions;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            var opts = _authOptions.CurrentValue;

            await UpdateUserEmailsAsync(db, opts, ct);
            await SeedDemoSongsIfEmptyAsync(db, ct);
        }
        catch (Exception ex)
        {
            // Don't crash startup on seed errors — log and continue. Without the seed, the demo
            // user just sees an empty library.
            _logger.LogError(ex, "Demo seeding failed; continuing startup.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task UpdateUserEmailsAsync(MusicHoarderDbContext db, AuthOptions opts, CancellationToken ct)
    {
        // Users table has no query filter (it's identity, not tenant data), so the standard query
        // works.
        var users = await db.Users
            .Where(u => u.Id == WellKnownUsers.OwnerId || u.Id == WellKnownUsers.DemoId)
            .ToListAsync(ct);

        var changed = false;
        foreach (var user in users)
        {
            var desired = user.Role switch
            {
                UserRole.Owner => opts.OwnerEmail,
                UserRole.Demo => opts.DemoUserEmail,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(desired) || string.Equals(user.Email, desired, StringComparison.Ordinal))
                continue;
            user.Email = desired!;
            user.EmailNormalized = User.Normalize(desired!);
            changed = true;
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Updated user emails from Auth options.");
        }
    }

    private async Task SeedDemoSongsIfEmptyAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        // Bypass the per-user query filter — this hosted service has no HTTP user.
        var hasDemoSongs = await db.Songs
            .IgnoreQueryFilters()
            .AnyAsync(s => s.OwnerUserId == WellKnownUsers.DemoId, ct);
        if (hasDemoSongs) return;

        var seeds = LoadEmbeddedSeed();
        if (seeds.Count == 0) return;

        var now = DateTime.UtcNow;
        var destinationRoot = "/demo/destination";

        foreach (var seed in seeds)
        {
            var fileName = $"{seed.Track:00} {Sanitize(seed.Title)}.mp3";
            var rel = $"{Sanitize(seed.Artist)}/{Sanitize(seed.Album)} ({seed.Year})/{fileName}";
            var sourcePath = $"demo://{Guid.NewGuid():N}";

            var song = new SongMetadata
            {
                OwnerUserId = WellKnownUsers.DemoId,
                IsSynthetic = true,
                SourcePath = sourcePath,
                FileSizeBytes = 4_500_000 + (seed.DurationMs ?? 0) * 30,
                FileName = fileName,
                Extension = ".mp3",
                LastModifiedUtc = now,
                IndexedAtUtc = now,
                Artist = seed.Artist,
                AlbumArtist = seed.Artist,
                Album = seed.Album,
                Title = seed.Title,
                Year = seed.Year,
                TrackNumber = seed.Track,
                DurationMs = seed.DurationMs,
                DurationSeconds = seed.DurationMs.HasValue ? seed.DurationMs / 1000 : null,
                Bitrate = 320,
                Fingerprint = "demo-fingerprint-" + Guid.NewGuid().ToString("N")[..12],
                EnrichmentStatus = EnrichmentStatus.Matched,
                MatchedBy = "Demo",
                MatchConfidence = 0.99,
                EnrichedAtUtc = now,
                LibraryBuildStatus = LibraryBuildStatus.Done,
                LibraryBuiltAtUtc = now,
                DestinationPath = Path.Combine(destinationRoot, rel),
                PlainLyrics = seed.Lyrics,
                LyricsStatus = string.IsNullOrEmpty(seed.Lyrics) ? LyricsStatus.NotFound : LyricsStatus.Fetched,
            };
            db.Songs.Add(song);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} demo songs for the demo user.", seeds.Count);
    }

    private static List<DemoSeed> LoadEmbeddedSeed()
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = $"{asm.GetName().Name}.Auth.Resources.demo-songs.json";
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null) return new List<DemoSeed>();
        return JsonSerializer.Deserialize<List<DemoSeed>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new List<DemoSeed>();
    }

    private static string Sanitize(string s) =>
        new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());

    private sealed record DemoSeed(
        string Artist,
        string Album,
        string Title,
        int Year,
        int Track,
        [property: JsonPropertyName("durationMs")] int? DurationMs,
        string? Lyrics);
}
