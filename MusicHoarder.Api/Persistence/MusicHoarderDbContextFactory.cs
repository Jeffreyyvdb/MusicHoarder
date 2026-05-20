using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Used by EF Core design-time tooling (<c>dotnet ef migrations add</c>, scaffolders). The runtime
/// path goes through Aspire's <c>AddNpgsqlDbContext</c> which wires the real connection string
/// and <see cref="MusicHoarderDbContext"/>'s 2-arg constructor.
/// </summary>
public sealed class MusicHoarderDbContextFactory : IDesignTimeDbContextFactory<MusicHoarderDbContext>
{
    public MusicHoarderDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseNpgsql("Host=localhost;Database=musichoarderdb;Username=postgres;Password=postgres")
            .Options;
        return new MusicHoarderDbContext(options);
    }
}
