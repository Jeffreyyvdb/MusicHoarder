using MusicHoarder.Api.BackgroundWork.Insights;

namespace MusicHoarder.Api.BackgroundWork;

public static class BackgroundWorkServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundWork(this IServiceCollection services)
    {
        services.AddSingleton<BackgroundWorkStatus>();

        // One per id in BackgroundJobIds; a test fails if one is missing or doubled.
        services.AddScoped<IBackgroundJobInsight, LyricsInsight>();
        services.AddScoped<IBackgroundJobInsight, TranscriptionAllowanceInsight>();
        services.AddScoped<IBackgroundJobInsight, CoverArtInsight>();
        services.AddScoped<IBackgroundJobInsight, MusicVideosInsight>();
        services.AddScoped<IBackgroundJobInsight, QualityGradingInsight>();
        services.AddScoped<IBackgroundJobInsight, EnrichmentRetriesInsight>();
        services.AddScoped<IBackgroundJobInsight, AlbumFillFollowsInsight>();
        return services;
    }
}
