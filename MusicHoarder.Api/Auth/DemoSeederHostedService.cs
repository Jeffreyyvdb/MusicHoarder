using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Auth;

/// <summary>
/// Runs once on startup after migrations. Responsibilities:
/// <list type="bullet">
///   <item>Update the Owner + Demo user rows' email fields from <see cref="AuthOptions.OwnerEmail"/>
///   / <see cref="AuthOptions.DemoUserEmail"/>. Lets you change your email by re-deploying.</item>
///   <item>Seed the owner's pre-registered passkey from <see cref="AuthOptions.OwnerSeedCredentialJson"/>
///   when configured (idempotent — skipped once the owner has any credential), so empty-DB
///   environments like per-PR previews accept the owner's existing passkey without re-registering.</item>
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
            await SeedOwnerCredentialIfConfiguredAsync(db, opts, ct);
            await SeedDemoSongsIfEmptyAsync(db, ct);
            await SeedDemoReviewSongsIfEmptyAsync(db, ct);
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

    private async Task SeedOwnerCredentialIfConfiguredAsync(MusicHoarderDbContext db, AuthOptions opts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(opts.OwnerSeedCredentialJson)) return;

        // Idempotent: once the owner has any passkey (the seeded one, or one registered live) leave
        // it alone. Per-PR previews start with an empty DB, so this seeds exactly once per env.
        var ownerHasCredential = await db.WebAuthnCredentials
            .AnyAsync(c => c.UserId == WellKnownUsers.OwnerId, ct);
        if (ownerHasCredential) return;

        OwnerCredentialSeed? seed;
        try
        {
            seed = JsonSerializer.Deserialize<OwnerCredentialSeed>(
                opts.OwnerSeedCredentialJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Auth:OwnerSeedCredentialJson is not valid JSON; skipping passkey seed.");
            return;
        }

        if (seed is null || string.IsNullOrWhiteSpace(seed.CredentialId) || string.IsNullOrWhiteSpace(seed.PublicKey))
        {
            _logger.LogError("Auth:OwnerSeedCredentialJson is missing credentialId/publicKey; skipping passkey seed.");
            return;
        }

        byte[] credentialId, publicKey;
        try
        {
            credentialId = Convert.FromBase64String(seed.CredentialId);
            publicKey = Convert.FromBase64String(seed.PublicKey);
        }
        catch (FormatException ex)
        {
            _logger.LogError(ex, "Auth:OwnerSeedCredentialJson credentialId/publicKey are not valid base64; skipping passkey seed.");
            return;
        }

        db.WebAuthnCredentials.Add(new WebAuthnCredential
        {
            Id = Guid.NewGuid(),
            UserId = WellKnownUsers.OwnerId,
            CredentialId = credentialId,
            PublicKey = publicKey,
            SignCount = seed.SignCount,
            AaGuid = seed.AaGuid,
            Transports = string.IsNullOrWhiteSpace(seed.Transports) ? null : seed.Transports,
            DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName) ? "Seeded owner passkey" : seed.DisplayName!,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded owner passkey from Auth:OwnerSeedCredentialJson.");
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

    /// <summary>
    /// Seed a handful of <see cref="EnrichmentStatus.NeedsReview"/> songs (each with competing
    /// provider-attempt candidates) for the demo user so the manual-review screen has realistic
    /// data. Guarded independently from the matched-songs seed so it also backfills demo databases
    /// that were already seeded before this seed existed.
    /// </summary>
    private async Task SeedDemoReviewSongsIfEmptyAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        var hasReviewSongs = await db.Songs
            .IgnoreQueryFilters()
            .AnyAsync(
                s => s.OwnerUserId == WellKnownUsers.DemoId && s.EnrichmentStatus == EnrichmentStatus.NeedsReview,
                ct);
        if (hasReviewSongs) return;

        var now = DateTime.UtcNow;
        // Distinct providers per candidate — there is a unique index on (SongId, Provider).
        var providers = new[]
        {
            EnrichmentProvider.AcoustID,
            EnrichmentProvider.SpotifyAPI,
            EnrichmentProvider.MusicBrainzWeb,
            EnrichmentProvider.Tracker,
        };

        foreach (var seed in ReviewSeeds)
        {
            var top = seed.Candidates.Length > 0 ? seed.Candidates[0] : null;
            var multipleMatches = seed.Reason == "multiple_matches";
            var warnings = multipleMatches
                ? new List<string> { "Multiple candidate matches with similar confidence", "Competing releases found" }
                : new List<string>();

            var song = new SongMetadata
            {
                OwnerUserId = WellKnownUsers.DemoId,
                IsSynthetic = true,
                SourcePath = $"/demo/source/{seed.Folder}/{seed.FileName}",
                FileName = seed.FileName,
                Extension = "." + seed.Format.ToLowerInvariant(),
                FileSizeBytes = seed.FileSizeBytes,
                LastModifiedUtc = now,
                IndexedAtUtc = now,
                Artist = string.IsNullOrEmpty(seed.EmbArtist) ? null : seed.EmbArtist,
                Album = string.IsNullOrEmpty(seed.EmbAlbum) ? null : seed.EmbAlbum,
                Title = string.IsNullOrEmpty(seed.EmbTitle) ? null : seed.EmbTitle,
                Year = seed.EmbYear,
                DurationSeconds = seed.DurationSeconds,
                DurationMs = seed.DurationSeconds * 1000,
                Bitrate = seed.Bitrate,
                Fingerprint = seed.Fingerprint,
                EnrichmentStatus = EnrichmentStatus.NeedsReview,
                MatchedBy = top is null ? null : top.Source,
                MatchConfidence = top?.Score,
                MatchWarnings = warnings.Count > 0 ? JsonSerializer.Serialize(warnings) : null,
                EnrichedAtUtc = now,
                LibraryBuildStatus = LibraryBuildStatus.Pending,
            };

            for (var i = 0; i < seed.Candidates.Length; i++)
            {
                var c = seed.Candidates[i];
                var result = new EnrichmentProviderResult(
                    Artist: c.Artist,
                    AlbumArtist: null,
                    Title: c.Title,
                    Year: c.Year,
                    TrackNumber: null,
                    MusicBrainzId: null,
                    MusicBrainzReleaseId: null,
                    SpotifyId: null,
                    AcoustIdTrackId: null,
                    Isrc: null,
                    MatchedBy: c.Source,
                    MatchConfidence: c.Score,
                    MatchWarnings: new List<string>(),
                    RecommendedStatus: EnrichmentStatus.NeedsReview,
                    Album: c.Album);

                song.ProviderAttempts.Add(new SongProviderAttempt
                {
                    Provider = providers[i % providers.Length],
                    Status = ProviderAttemptStatus.Matched,
                    AttemptedAtUtc = now,
                    MatchedDataJson = JsonSerializer.Serialize(result),
                });
            }

            db.Songs.Add(song);
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Seeded {Count} demo review songs for the demo user.", ReviewSeeds.Length);
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

    /// <summary>Shape of <see cref="AuthOptions.OwnerSeedCredentialJson"/>. Byte fields are base64.</summary>
    private sealed record OwnerCredentialSeed(
        string CredentialId,
        string PublicKey,
        Guid AaGuid,
        long SignCount,
        string? Transports,
        string? DisplayName);

    private sealed record DemoSeed(
        string Artist,
        string Album,
        string Title,
        int Year,
        int Track,
        [property: JsonPropertyName("durationMs")] int? DurationMs,
        string? Lyrics);

    private sealed record ReviewSeed(
        string Folder,
        string FileName,
        string Format,
        int FileSizeBytes,
        int DurationSeconds,
        int Bitrate,
        string? Fingerprint,
        string Reason,
        string? EmbTitle,
        string? EmbArtist,
        string? EmbAlbum,
        int? EmbYear,
        ReviewCandidateSeed[] Candidates);

    private sealed record ReviewCandidateSeed(
        string Source,
        double Score,
        string Title,
        string Artist,
        string Album,
        int Year);

    // Messy, freshly-dumped files awaiting a human decision — mirrors the design's review queue.
    private static readonly ReviewSeed[] ReviewSeeds =
    {
        new("_incoming/01_dump", "track_047.mp3", "MP3", 8_400_000, 314, 320,
            "AQADtKqaZFEoS1E4hUeS", "low_confidence", "Track 47", "unknown", null, null,
            new[]
            {
                new ReviewCandidateSeed("AcoustID", 0.62, "1969", "Boards of Canada", "Geogaddi", 2002),
                new ReviewCandidateSeed("Discogs", 0.58, "1969", "Boards of Canada", "Geogaddi (Reissue)", 2013),
                new ReviewCandidateSeed("MusicBrainz", 0.41, "1969 (live)", "Boards of Canada", "Trans Canada Highway", 2006),
            }),
        new("_incoming/02_usb", "IMG_2014_audio.m4a", "M4A", 14_100_000, 221, 256,
            "AQADtMqcZFEoTVE8hUeS", "multiple_matches", null, null, null, null,
            new[]
            {
                new ReviewCandidateSeed("AcoustID", 0.71, "Avril 14th", "Aphex Twin", "Drukqs", 2001),
                new ReviewCandidateSeed("Spotify", 0.69, "Avril 14th (Piano)", "Aphex Twin", "Piano Works", 2003),
                new ReviewCandidateSeed("Discogs", 0.55, "Avril 14", "Aphex Twin", "Drukqs (Japanese ed.)", 2002),
            }),
        new("_incoming/01_dump/various", "08_-_unnamed.flac", "FLAC", 34_700_000, 248, 911,
            "AQADtNqdZFEoTlE+hUeS", "low_confidence", "08", null, "Various", null,
            new[]
            {
                new ReviewCandidateSeed("AcoustID", 0.54, "Strawberry Swing", "Frank Ocean", "Nostalgia, Ultra.", 2011),
                new ReviewCandidateSeed("MusicBrainz", 0.49, "Strawberry Swing", "Coldplay", "Viva la Vida", 2008),
            }),
        new("_incoming/03_phone_dump", "JLOLN0301.wav", "WAV", 52_300_000, 288, 1411,
            null, "no_fingerprint", null, null, null, null,
            Array.Empty<ReviewCandidateSeed>()),
        new("_incoming/02_usb/bootlegs", "live_set_aug.flac", "FLAC", 127_800_000, 862, 988,
            "AQADtOqsZFEoXVFchUeS", "multiple_matches", "Live in Brixton 08", null, null, null,
            new[]
            {
                new ReviewCandidateSeed("AcoustID", 0.68, "Live in Brixton — Side A", "Massive Attack", "Bootleg 2008", 2008),
                new ReviewCandidateSeed("Spotify", 0.66, "Brixton Academy (full)", "Massive Attack", "Unofficial live", 2008),
                new ReviewCandidateSeed("Discogs", 0.52, "Live at Brixton", "Massive Attack", "Heligoland Tour", 2010),
            }),
        new("_incoming/01_dump", "unknown_artist - 02 - .ogg", "OGG", 6_200_000, 192, 192,
            "AQADtPqtZFEoXlFehUeS", "low_confidence", null, "unknown_artist", null, null,
            new[]
            {
                new ReviewCandidateSeed("AcoustID", 0.59, "Roygbiv", "Boards of Canada", "Music Has the Right to Children", 1998),
            }),
    };
}
