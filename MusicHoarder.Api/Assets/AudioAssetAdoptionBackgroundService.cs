using Microsoft.Extensions.Options;
using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Assets;

/// <summary>
/// Runs <see cref="AudioAssetAdoptionService"/> in passes: page after page until no adoptable song is
/// left, then waits <see cref="MusicEnricherOptions.AudioAssetAdoptionIntervalMinutes"/>. It does nothing
/// unless <see cref="MusicEnricherOptions.EnableAudioAssetAdoption"/> is on when the API starts, because
/// a first pass over a large library on a network mount reads every file once.
/// </summary>
public sealed class AudioAssetAdoptionBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<MusicEnricherOptions> options,
    BackgroundWorkStatus status,
    ILogger<AudioAssetAdoptionBackgroundService> logger) : BackgroundService
{
    /// <summary>Lets startup, migrations and the first scan settle before the first pass.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableAudioAssetAdoption)
            return;

        var delay = InitialDelay;
        status.Idle(BackgroundJobIds.AudioAssets, DateTime.UtcNow + delay);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromMinutes(options.Value.AudioAssetAdoptionIntervalMinutes);
            status.Working(BackgroundJobIds.AudioAssets);
            try
            {
                var linked = await RunPassAsync(stoppingToken);
                status.Idle(BackgroundJobIds.AudioAssets, DateTime.UtcNow + delay, worked: linked > 0);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audio asset adoption pass failed");
                status.Failing(BackgroundJobIds.AudioAssets, ex, DateTime.UtcNow + delay);
            }
        }
    }

    private async Task<int> RunPassAsync(CancellationToken ct)
    {
        var afterSongId = 0;
        var linked = 0;
        while (true)
        {
            using var scope = scopeFactory.CreateScope();
            var adoption = scope.ServiceProvider.GetRequiredService<AudioAssetAdoptionService>();
            if (await adoption.AdoptNextPageAsync(afterSongId, ct) is not { } page)
                break;
            afterSongId = page.LastSongId;
            linked += page.Linked;
        }

        logger.LogInformation("Audio asset adoption pass linked {Linked} songs", linked);
        return linked;
    }
}
