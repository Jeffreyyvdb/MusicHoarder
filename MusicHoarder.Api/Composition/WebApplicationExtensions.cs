using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Composition;

public static class WebApplicationExtensions
{
    /// <summary>
    /// Applies pending EF Core migrations on startup. For single-instance homelab deployments this is safe.
    /// </summary>
    public static async Task ApplyPendingMigrationsAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public static WebApplication MapMusicHoarderEndpoints(this WebApplication app)
    {
        app.MapLegacyScanEndpoints();
        app.MapEnrichmentEndpoints();
        app.MapDashboardEndpoints();
        app.MapSongsEndpoints();
        return app;
    }
}
