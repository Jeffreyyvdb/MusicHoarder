using System.IO.Abstractions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Scanner;
using MusicHoarder.ServiceDefaults;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services
    .AddOptions<MusicEnricherOptions>()
    .BindConfiguration(MusicEnricherOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.AddNpgsqlDbContext<MusicHoarderDbContext>(connectionName: "musichoarderdb");

builder.Services.AddSingleton(Channel.CreateUnbounded<ScanRequest>());
builder.Services.AddSingleton<ScanProgressTracker>();
builder.Services.AddSingleton<IFpcalcService, FpcalcService>();

builder.Services.AddHostedService<ScannerBackgroundService>();

builder.Services.AddScoped<IFileSystem, FileSystem>();
builder.Services.AddScoped<IFileScanner, FileScanner>();
builder.Services.AddScoped<IIndexService, IndexService>();

builder.Services.AddHttpClient<IAcoustIdService, AcoustIdService>(client =>
{
    client.BaseAddress = new Uri("https://api.acoustid.org/");
});

builder.Services.AddOpenApi();

var app = builder.Build();

var musicEnricherOptions = app.Services.GetRequiredService<IOptions<MusicEnricherOptions>>().Value;
Directory.CreateDirectory(musicEnricherOptions.TempDirectory);

app.MapDefaultEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseHttpsRedirection();

app.MapPost("/scan", async (Channel<ScanRequest> channel) =>
{
    var scanId = Guid.NewGuid();
    await channel.Writer.WriteAsync(new ScanRequest(scanId));
    return Results.Accepted($"/scan/{scanId}/progress", new { scanId });
});

app.MapGet("/scan/{scanId}/progress", (Guid scanId, ScanProgressTracker tracker) =>
{
    var state = tracker.GetCurrent();
    if (state is null || state.ScanId != scanId)
        return Results.NotFound(new { message = "No scan found with that id." });

    return Results.Ok(state);
});

app.MapGet("/stats", async (MusicHoarderDbContext db) =>
{
    var active = db.Songs.Where(s => s.DeletedAtUtc == null);
    var totalCount = await active.CountAsync();
    var deletedCount = await db.Songs.CountAsync(s => s.DeletedAtUtc != null);

    var storage = await active
        .GroupBy(_ => 1)
        .Select(g => new
        {
            TotalBytes = g.Sum(s => s.FileSizeBytes),
            AvgBytes = (long)g.Average(s => s.FileSizeBytes),
        })
        .FirstOrDefaultAsync();

    var duration = await active
        .Where(s => s.DurationSeconds != null)
        .GroupBy(_ => 1)
        .Select(g => new
        {
            TotalSeconds = g.Sum(s => s.DurationSeconds ?? 0),
            TrackCountWithDuration = g.Count(),
        })
        .FirstOrDefaultAsync();

    var byExtensionRaw = await active
        .GroupBy(s => s.Extension)
        .Select(g => new { Extension = g.Key, Count = g.Count() })
        .ToListAsync();
    var byExtension = byExtensionRaw
        .GroupBy(x => x.Extension?.ToLowerInvariant() ?? "")
        .Select(g => new { Extension = g.Key, Count = g.Sum(x => x.Count) })
        .OrderByDescending(x => x.Count)
        .ToList();

    var enrichment = await active
        .GroupBy(_ => 1)
        .Select(g => new
        {
            WithFingerprint = g.Count(s => s.Fingerprint != null && s.Fingerprint != ""),
            WithMusicBrainzId = g.Count(s => s.MusicBrainzId != null && s.MusicBrainzId != ""),
            WithSpotifyId = g.Count(s => s.SpotifyId != null && s.SpotifyId != ""),
            WithIsrc = g.Count(s => s.Isrc != null && s.Isrc != ""),
            WithArtist = g.Count(s => s.Artist != null && s.Artist != ""),
            WithAlbum = g.Count(s => s.Album != null && s.Album != ""),
            WithTitle = g.Count(s => s.Title != null && s.Title != ""),
        })
        .FirstOrDefaultAsync();

    var indexWindow = await active
        .GroupBy(_ => 1)
        .Select(g => new
        {
            OldestIndexed = g.Min(s => s.IndexedAtUtc),
            NewestIndexed = g.Max(s => s.IndexedAtUtc),
            OldestModified = g.Min(s => s.LastModifiedUtc),
            NewestModified = g.Max(s => s.LastModifiedUtc),
        })
        .FirstOrDefaultAsync();

    var stats = new
    {
        Tracks = new
        {
            Total = totalCount,
            Deleted = deletedCount,
        },
        Storage = storage == null ? null : new
        {
            TotalBytes = storage.TotalBytes,
            TotalGiB = Math.Round(storage.TotalBytes / (1024.0 * 1024.0 * 1024.0), 2),
            AverageBytesPerTrack = storage.AvgBytes,
        },
        Duration = duration == null ? null : new
        {
            TotalSeconds = duration.TotalSeconds,
            TotalHours = Math.Round(duration.TotalSeconds / 3600.0, 1),
            TracksWithDuration = duration.TrackCountWithDuration,
            AverageSecondsPerTrack = duration.TrackCountWithDuration > 0
                ? Math.Round(duration.TotalSeconds / (double)duration.TrackCountWithDuration, 1)
                : (double?)null,
        },
        ByExtension = byExtension,
        Enrichment = enrichment == null ? null : new
        {
            WithFingerprint = enrichment.WithFingerprint,
            WithMusicBrainzId = enrichment.WithMusicBrainzId,
            WithSpotifyId = enrichment.WithSpotifyId,
            WithIsrc = enrichment.WithIsrc,
            WithArtist = enrichment.WithArtist,
            WithAlbum = enrichment.WithAlbum,
            WithTitle = enrichment.WithTitle,
            FingerprintPct = totalCount > 0 ? Math.Round(100.0 * enrichment.WithFingerprint / totalCount, 1) : 0,
            MusicBrainzPct = totalCount > 0 ? Math.Round(100.0 * enrichment.WithMusicBrainzId / totalCount, 1) : 0,
        },
        IndexWindow = indexWindow == null ? null : new
        {
            OldestIndexedUtc = indexWindow.OldestIndexed,
            NewestIndexedUtc = indexWindow.NewestIndexed,
            OldestFileModifiedUtc = indexWindow.OldestModified,
            NewestFileModifiedUtc = indexWindow.NewestModified,
        },
    };

    return Results.Ok(stats);
});

app.Run();