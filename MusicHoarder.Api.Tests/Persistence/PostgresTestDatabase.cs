using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MusicHoarder.Api.Persistence;
using Npgsql;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// A test that needs what only Postgres does (a unique index enforced, a conflict clause), reported as
/// skipped unless <c>MUSICHOARDER_TEST_POSTGRES</c> names a server it may create databases on, e.g.
/// <c>Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres</c>.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(PostgresTestDatabase.ServerConnectionString))
            Skip = $"{PostgresTestDatabase.EnvironmentVariable} is not set";
    }
}

/// <summary>A throwaway database migrated to the current schema, dropped on dispose.</summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    public const string EnvironmentVariable = "MUSICHOARDER_TEST_POSTGRES";

    public static string? ServerConnectionString => Environment.GetEnvironmentVariable(EnvironmentVariable);

    private readonly string _connectionString;
    private readonly string _name;

    private PostgresTestDatabase(string name, string connectionString)
    {
        _name = name;
        _connectionString = connectionString;
    }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var name = $"mh_test_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(ServerConnectionString) { Database = name }.ConnectionString;
        var database = new PostgresTestDatabase(name, connectionString);
        await using var db = database.NewContext();
        await db.Database.MigrateAsync();
        return database;
    }

    /// <summary>A context on this database; with <paramref name="log"/>, EF's warnings and errors are written to it.</summary>
    public MusicHoarderDbContext NewContext(ILogger? log = null, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>().UseNpgsql(_connectionString);
        if (log is not null)
            options.LogTo((_, level) => level >= LogLevel.Warning,
                data => log.Log(data.LogLevel, data.EventId, data.ToString(), null, (s, _) => s));
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        return new MusicHoarderDbContext(options.Options);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var server = new NpgsqlConnection(ServerConnectionString);
        await server.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", server);
        await drop.ExecuteNonQueryAsync();
    }
}
