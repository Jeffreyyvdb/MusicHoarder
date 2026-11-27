using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MusicHoarder.Api.Catalog;

public static class CatalogServiceCollectionExtensions
{
    public static IServiceCollection AddCatalog(this IServiceCollection services)
    {
        services.AddSingleton(_ =>
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MusicHoarder-Catalog");
            return new CatalogHttpClient(client);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CatalogPreviewService>();
        services.AddSingleton<ICatalogAudioLocator, YtDlpAudioLocator>();
        services.AddSingleton<ICatalogAudioResolver, CatalogAudioResolver>();
        return services;
    }
}
