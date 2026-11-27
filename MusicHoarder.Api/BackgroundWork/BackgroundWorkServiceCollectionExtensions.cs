using Microsoft.Extensions.DependencyInjection.Extensions;
using MusicHoarder.Api.BackgroundWork.Insights;

namespace MusicHoarder.Api.BackgroundWork;

public static class BackgroundWorkServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundWork(this IServiceCollection services)
    {
        services.TryAddSingleton<BackgroundWorkStatus>();
        services.TryAddSingleton<MusicVideoOutcomeMemo>();
        services.AddScoped<MusicVideoBacklogLoader>();
        services.TryAddSingleton<BackgroundWorkMetrics>();

        services.AddScoped<IBackgroundJobInsight, LyricsInsight>();
        services.AddScoped<IBackgroundJobInsight, TranscriptionAllowanceInsight>();
        services.AddScoped<IBackgroundJobInsight, CoverArtInsight>();
        services.AddScoped<IBackgroundJobInsight, MusicVideosInsight>();
        services.AddScoped<IBackgroundJobInsight, MusicVideoDownloadsInsight>();
        services.AddScoped<IBackgroundJobInsight, QualityGradingInsight>();
        services.AddScoped<IBackgroundJobInsight, EnrichmentRetriesInsight>();
        services.AddScoped<IBackgroundJobInsight, AlbumFillFollowsInsight>();
        services.AddScoped<IBackgroundJobInsight, AudioAssetsInsight>();
        return services;
    }
}
