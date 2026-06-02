using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

public sealed class AlbumGradingService(
    IServiceScopeFactory scopeFactory,
    IChatCompletionClient chatClient,
    IAlbumGradingDossierFactory dossierFactory,
    IOwnerLookupService ownerLookup,
    IOptions<MusicEnricherOptions> enricherOptions,
    IOptionsMonitor<QualityGradingOptions> options,
    ILogger<AlbumGradingService> logger) : IAlbumGradingService
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    public async Task<GradeAlbumResult> GradeAlbumAsync(int canonicalAlbumId, bool force = false, CancellationToken ct = default)
    {
        var opts = options.CurrentValue;
        var owner = ownerLookup.OwnerUserId;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var album = await db.CanonicalAlbums
            .Include(a => a.Tracks)
            .FirstOrDefaultAsync(a => a.Id == canonicalAlbumId, ct);

        // Only a fetched (linked) album can be graded — there's nothing to judge otherwise.
        if (album is null || album.Status != CanonicalAlbumStatus.Fetched)
            return new GradeAlbumResult(GradeOutcome.NotFound, null);

        var ownedSongs = await LoadOwnedSongsAsync(db, owner, album, ct);
        var latestVerdicts = await LoadLatestSongVerdictsAsync(db, ownedSongs, ct);

        var dossier = dossierFactory.Build(album, ownedSongs, latestVerdicts, enricherOptions.Value.IdentityTitleThreshold);
        var fingerprint = Fingerprint(dossier);

        var latest = await db.CanonicalAlbumQualityGrades
            .IgnoreQueryFilters()
            .Where(g => g.CanonicalAlbumId == canonicalAlbumId && g.OwnerUserId == owner)
            .OrderByDescending(g => g.GradedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (!force
            && latest is not null
            && latest.InputFingerprint == fingerprint
            && latest.Model == opts.Model
            && latest.PromptVersion == AlbumGradingPrompt.Version)
        {
            return new GradeAlbumResult(GradeOutcome.Skipped, latest);
        }

        if (!chatClient.IsConfigured)
            return new GradeAlbumResult(GradeOutcome.NotConfigured, latest);

        GradingResult parsed;
        string rawContent;
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await chatClient.CompleteAsync(
                new ChatCompletionRequest(
                    AlbumGradingPrompt.BuildMessages(dossier),
                    opts.Temperature,
                    opts.MaxOutputTokens),
                ct);
            rawContent = result.Content;
            parsed = QualityGradingPrompt.Parse(rawContent);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Album grading failed for canonical album {AlbumId}", canonicalAlbumId);
            return new GradeAlbumResult(GradeOutcome.Failed, latest, ex.Message, ClassifyError(ex));
        }
        sw.Stop();

        var grade = new CanonicalAlbumQualityGrade
        {
            CanonicalAlbumId = album.Id,
            OwnerUserId = owner,
            Score = parsed.Score,
            Verdict = parsed.Verdict,
            Summary = parsed.Summary,
            IssuesJson = parsed.Issues.Count > 0 ? JsonSerializer.Serialize(parsed.Issues, CompactJson) : null,
            Model = opts.Model,
            PromptVersion = AlbumGradingPrompt.Version,
            InputFingerprint = fingerprint,
            OwnedTrackCount = ownedSongs.Count,
            CanonicalTrackCount = album.Tracks.Count,
            DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
            RawResponseJson = rawContent.Length > 8192 ? rawContent[..8192] : rawContent,
            GradedAtUtc = DateTime.UtcNow,
        };

        db.CanonicalAlbumQualityGrades.Add(grade);
        await db.SaveChangesAsync(ct);

        return new GradeAlbumResult(GradeOutcome.Graded, grade);
    }

    public async Task<AlbumGradingDossier?> BuildDossierAsync(int canonicalAlbumId, CancellationToken ct = default)
    {
        var owner = ownerLookup.OwnerUserId;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var album = await db.CanonicalAlbums
            .AsNoTracking()
            .Include(a => a.Tracks)
            .FirstOrDefaultAsync(a => a.Id == canonicalAlbumId, ct);
        if (album is null || album.Status != CanonicalAlbumStatus.Fetched)
            return null;

        var ownedSongs = await LoadOwnedSongsAsync(db, owner, album, ct);
        var latestVerdicts = await LoadLatestSongVerdictsAsync(db, ownedSongs, ct);
        return dossierFactory.Build(album, ownedSongs, latestVerdicts, enricherOptions.Value.IdentityTitleThreshold);
    }

    /// <summary>The owner's Matched songs whose normalized (albumArtist??artist, album) identity is this album.</summary>
    private static async Task<List<OwnedSongForGrading>> LoadOwnedSongsAsync(
        MusicHoarderDbContext db, Guid owner, CanonicalAlbum album, CancellationToken ct)
    {
        var candidates = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OwnerUserId == owner && s.DeletedAtUtc == null && !s.IsSynthetic
                && s.EnrichmentStatus == EnrichmentStatus.Matched
                && s.Album != null && s.Album != "")
            .Select(s => new { s.Id, s.AlbumArtist, s.Artist, s.Album, s.Title, s.DiscNumber, s.TrackNumber, s.DurationSeconds, s.MusicBrainzId })
            .ToListAsync(ct);

        return candidates
            .Where(s => TitleNormalizer.NormalizeForSearch(s.AlbumArtist ?? s.Artist) == album.ArtistKey
                && TitleNormalizer.NormalizeForSearch(s.Album) == album.AlbumKey)
            .Select(s => new OwnedSongForGrading(
                s.Id, s.Title, s.Artist, s.DiscNumber, s.TrackNumber, s.DurationSeconds, s.MusicBrainzId))
            .ToList();
    }

    private static async Task<Dictionary<int, SongQualityVerdict>> LoadLatestSongVerdictsAsync(
        MusicHoarderDbContext db, List<OwnedSongForGrading> ownedSongs, CancellationToken ct)
    {
        if (ownedSongs.Count == 0)
            return [];

        var ownedIds = ownedSongs.Select(o => o.Id).ToList();
        var grades = await db.SongQualityGrades
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(g => ownedIds.Contains(g.SongId))
            .Select(g => new { g.SongId, g.Verdict, g.GradedAtUtc })
            .ToListAsync(ct);

        return grades
            .GroupBy(g => g.SongId)
            .ToDictionary(grp => grp.Key, grp => grp.OrderByDescending(x => x.GradedAtUtc).First().Verdict);
    }

    private static string ClassifyError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.PaymentRequired } => "out_of_credits",
        HttpRequestException => "http_error",
        InvalidOperationException => "empty_response",
        _ => "error",
    };

    private static string Fingerprint(AlbumGradingDossier dossier)
    {
        var json = JsonSerializer.Serialize(dossier, CompactJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
