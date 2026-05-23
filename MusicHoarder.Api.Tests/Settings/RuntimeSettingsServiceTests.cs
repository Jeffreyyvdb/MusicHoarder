using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Tests.Settings;

public class RuntimeSettingsServiceTests
{
    [Fact]
    public async Task QualityGradingEnabled_FallsBackToConfiguredDefault_WhenNoRow()
    {
        var db = CreateDb();
        var service = CreateService(db, qualityEnabledDefault: true);

        var effective = await service.GetAsync();

        Assert.True(effective.QualityGradingEnabled);
    }

    [Fact]
    public async Task QualityGradingEnabled_RowOverridesConfiguredDefault()
    {
        var db = CreateDb();
        // Configured default is on, but the persisted overlay turns it off.
        var service = CreateService(db, qualityEnabledDefault: true);

        var effective = await service.UpdateAsync(new RuntimeSettingsUpdate { QualityGradingEnabled = false });

        Assert.False(effective.QualityGradingEnabled);
        Assert.False((await service.GetAsync()).QualityGradingEnabled);
        Assert.False(await db.RuntimeSettings.Select(r => r.QualityGradingEnabled).SingleAsync());
    }

    // --- helpers ---

    private static MusicHoarderDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static RuntimeSettingsService CreateService(MusicHoarderDbContext db, bool qualityEnabledDefault) =>
        new(new SimpleScopeFactory(db),
            new TestOptionsMonitor<MusicEnricherOptions>(new MusicEnricherOptions
            {
                SourceDirectory = "/src",
                DestinationDirectory = "/dst",
            }),
            new TestOptionsMonitor<QualityGradingOptions>(new QualityGradingOptions { Enabled = qualityEnabledDefault }));

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class SimpleScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(new Provider(db));
        private sealed class Scope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = provider;
            public void Dispose() { }
        }
        private sealed class Provider(MusicHoarderDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(MusicHoarderDbContext) ? db : null;
        }
    }
}
