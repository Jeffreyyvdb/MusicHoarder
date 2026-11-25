namespace MusicHoarder.Api.BackgroundWork;

public static class BackgroundWorkServiceCollectionExtensions
{
    public static IServiceCollection AddBackgroundWork(this IServiceCollection services)
    {
        services.AddSingleton<BackgroundWorkStatus>();
        return services;
    }
}
