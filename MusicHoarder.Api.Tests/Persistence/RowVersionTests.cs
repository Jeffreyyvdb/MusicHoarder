using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Auth;
using MusicHoarder.Api.Tests.Endpoints;
using MusicHoarder.Api.Tests.Sharing;
using Npgsql;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// The row versions the library lists and the Inbox's name counts are stamped by
/// (<see cref="RowVersions"/>). Their risk is one-sided: a version that stays put after a write its
/// reader would have noticed serves an out-of-date library. So these pin, on a real Postgres, that
/// a write to any column not on an ignore list moves the version, and that nothing on a list is a
/// column its reader reads.
/// </summary>
public class RowVersionTests
{
    private static readonly CurrentUser Owner = TestCurrentUserAccessor.OwnerUser;
    private static readonly CurrentUser Friend = TestCurrentUserAccessor.FriendUser;

    // No Postgres needed: the detectors load exactly InboxNameCounter.Columns, and its stamp
    // statement filters on three more.
    [Fact]
    public void The_name_version_ignores_no_column_the_name_detectors_read()
    {
        var read = InboxNameCounter.Columns.Names.Concat(
        [
            nameof(SongMetadata.Id),
            nameof(SongMetadata.OwnerUserId),
            nameof(SongMetadata.DeletedAtUtc),
            nameof(SongMetadata.IsSynthetic),
        ]);

        Assert.Empty(RowVersions.Songs.Version(RowVersions.Names).Ignored.Intersect(read));
    }

    // The triggers compare rows as JSON, keyed by column name, so a list must spell a column the
    // way the table does, and a key or a reference is never bookkeeping.
    [Fact]
    public void Every_ignored_name_is_an_ordinary_column_of_its_table()
    {
        using var db = new MusicHoarderDbContext(
            new DbContextOptionsBuilder<MusicHoarderDbContext>().UseNpgsql("Host=unused").Options);

        foreach (var table in RowVersions.Tables)
        {
            var entity = db.Model.FindEntityType(table.Entity)!;
            Assert.Equal(table.Table, entity.GetTableName());
            foreach (var version in table.Versions)
            {
                Assert.Equal(version.Ignored, table.IgnoredColumns(db.Model, version));
                Assert.DoesNotContain(version.Ignored, name =>
                    entity.FindProperty(name) is { } property && (property.IsKey() || property.IsForeignKey()));
            }
        }
    }

    // What a database runs is what a migration installed. A list changed in RowVersions without a
    // migration would be a list production never gets.
    [PostgresFact]
    public async Task The_newest_migration_installed_the_functions_RowVersions_generates()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();

        foreach (var table in RowVersions.Tables)
        {
            var installed = await Scalar<string>(db,
                $"SELECT prosrc FROM pg_proc WHERE proname = '{table.TriggerName}'");
            Assert.True(table.FunctionBody() == installed,
                $"{table.TriggerName} is not what RowVersions generates. Add a migration that runs:\n{table.InstallSql()}");
            Assert.Equal('O', await Scalar<char>(db,
                $"SELECT tgenabled FROM pg_trigger WHERE tgname = '{table.TriggerName}' AND tgrelid = '\"{table.Table}\"'::regclass"));
        }
    }

    // Every column of every versioned table, one write each. The ignore lists are the only columns
    // allowed to leave a version alone, so a column added tomorrow counts as visible without
    // anyone listing it.
    [PostgresFact]
    public async Task A_write_moves_a_version_unless_it_changes_only_columns_that_version_ignores()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await Seed(db);

        var wrong = new List<string>();
        foreach (var table in RowVersions.Tables)
        {
            var entity = db.Model.FindEntityType(table.Entity)!;
            var storeObject = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;
            var versionColumns = table.Versions.Select(v => v.Column).ToArray();
            var id = await Scalar<int>(db, $"SELECT min(\"Id\") FROM \"{table.Table}\"");
            async Task<long[]> Versions()
            {
                var versions = new long[versionColumns.Length];
                for (var i = 0; i < versions.Length; i++)
                    versions[i] = await Scalar<long>(db, $"SELECT \"{versionColumns[i]}\" FROM \"{table.Table}\" WHERE \"Id\" = {id}");
                return versions;
            }

            foreach (var property in entity.GetProperties().Where(p => !p.IsKey() && !p.IsForeignKey()))
            {
                var column = property.GetColumnName(storeObject)!;
                var before = await Versions();
                await Execute(db, $"UPDATE \"{table.Table}\" SET \"{column}\" = {Different(column, property.GetColumnType())} WHERE \"Id\" = {id}");
                var after = await Versions();

                for (var i = 0; i < versionColumns.Length; i++)
                {
                    var mayStay = versionColumns.Contains(column) || table.Versions[i].Ignored.Contains(column);
                    if (mayStay && after[i] != before[i])
                        wrong.Add($"{table.Table}.{versionColumns[i]} moved on a write to {column}, which it ignores");
                    if (!mayStay && after[i] <= before[i])
                        wrong.Add($"{table.Table}.{versionColumns[i]} stayed at {before[i]} on a write to {column}");
                }
            }
        }

        Assert.Empty(wrong);
    }

    // The other half of an ignore list: nothing on it is something a list shows. The owner's rows
    // are projected in SQL, so their statements must not name an ignored column at all. A granted
    // slice is loaded as whole rows and narrowed in memory, so for both callers every ignored
    // column is also rewritten on every row and the two lists must come out byte for byte the same.
    [PostgresFact]
    public async Task The_library_lists_show_nothing_their_version_ignores()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var seed = database.NewContext())
            await Seed(seed);
        var statements = new RecordedStatements();
        async Task<string> Lists(CurrentUser caller)
        {
            await using var db = database.NewContextFor(caller, caller == Owner ? [statements] : []);
            return await Written(await ListSongsCaller.Invoke(db, caller: caller))
                + await Written(await AlbumsEndpoints.ListAlbums(db, TestLibraryScope.For(caller), CancellationToken.None));
        }

        var asOwner = await Lists(Owner);
        var asFriend = await Lists(Friend);
        Assert.Contains("\"hasMusicVideo\":true", asOwner);
        Assert.Contains("On Sight", asFriend);
        Assert.Contains(statements.Sql, sql => sql.Contains("\"SongMusicVideos\""));
        Assert.Contains(statements.Sql, sql => sql.Contains("\"WishlistSources\""));

        var ignored = RowVersions.Tables
            .SelectMany(table => table.Version(RowVersions.List).Ignored.Select(column => (table.Table, Column: column, table.Entity)))
            .ToList();
        Assert.Empty(ignored.SelectMany(i => statements.Sql
            .Where(sql => sql.Contains($"\"{i.Column}\""))
            .Select(sql => $"{i.Table}.{i.Column} in: {sql[..Math.Min(sql.Length, 120)].ReplaceLineEndings(" ")}")));

        await using var writer = database.NewContext();
        foreach (var (table, column, entity) in ignored)
        {
            var type = writer.Model.FindEntityType(entity)!.FindProperty(column)!.GetColumnType();
            await Execute(writer, $"UPDATE \"{table}\" SET \"{column}\" = {Different(column, type)}");
            Assert.True(asOwner == await Lists(Owner), $"The owner's lists changed on a write to {table}.{column}.");
            Assert.True(asFriend == await Lists(Friend), $"A grantee's lists changed on a write to {table}.{column}.");
        }
    }

    [PostgresFact]
    public async Task The_library_stamp_survives_bookkeeping_writes_and_moves_on_visible_ones()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContextFor(Owner);
        await Seed(db);
        Task<string?> Stamp() => new LibraryChangeStamp(
            db,
            new TestCurrentUserAccessor(Owner),
            TestLibraryScope.For(Owner),
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions { SourceDirectory = "/source", DestinationDirectory = "/dest" }),
            Microsoft.Extensions.Options.Options.Create(new SyncOptions()),
            NullLogger<LibraryChangeStamp>.Instance).ComputeAsync(CancellationToken.None);

        var stamp = await Stamp();
        Assert.NotNull(stamp);

        // What the wishlist sync, the lyrics timing check and the builder write all day.
        await Execute(db, "UPDATE \"WishlistSources\" SET \"LastSyncedAtUtc\" = now()");
        await Execute(db, "UPDATE \"Songs\" SET \"LyricsSyncCheckedAtUtc\" = now(), \"LyricsSyncConfidence\" = 0.5, \"LyricsSyncProbeAttempts\" = \"LyricsSyncProbeAttempts\" + 1");
        await Execute(db, "UPDATE \"Songs\" SET \"LibraryBuildLastAttemptedAtUtc\" = now(), \"LastWrittenAtUtc\" = now()");
        await Execute(db, "UPDATE \"SongMusicVideos\" SET \"LetterboxFraction\" = 0.13");
        Assert.Equal(stamp, await Stamp());

        async Task AssertMoves(string write)
        {
            await Execute(db, write);
            var moved = await Stamp();
            Assert.True(moved != stamp, $"The stamp did not move on: {write}");
            stamp = moved;
        }

        await AssertMoves("UPDATE \"Songs\" SET \"Title\" = 'Renamed' WHERE \"Title\" = 'On Sight'");
        // Put back: the library reads as it did, and the stamp still must not.
        await AssertMoves("UPDATE \"Songs\" SET \"Title\" = 'On Sight' WHERE \"Title\" = 'Renamed'");
        // A bookkeeping column and a visible one in the same statement, as the builder writes them.
        await AssertMoves("UPDATE \"Songs\" SET \"LibraryBuildLastAttemptedAtUtc\" = now(), \"LibraryBuildStatus\" = 0 WHERE \"Title\" = 'On Sight'");
        await AssertMoves("UPDATE \"Songs\" SET \"PlayCount\" = \"PlayCount\" + 1 WHERE \"Title\" = 'On Sight'");
        await AssertMoves("UPDATE \"SongMusicVideos\" SET \"Status\" = 2, \"FilePath\" = NULL");
        await AssertMoves("UPDATE \"WishlistSources\" SET \"Name\" = 'Renamed playlist'");
        await AssertMoves("DELETE FROM \"SongMusicVideos\"");

        // A table whose trigger is not there (a schema built some other way) gives its rows no
        // version, and a row without one must count every write.
        await Execute(db, "ALTER TABLE \"WishlistSources\" DISABLE TRIGGER \"WishlistSources_RowVersions\"");
        await AssertMoves("INSERT INTO \"WishlistSources\" (\"OwnerUserId\", \"SourceType\", \"Name\", \"AutoSync\", \"CreatedAtUtc\") " +
            $"VALUES ('{Owner.Id}', 1, 'Untracked', false, now())");
        await AssertMoves("UPDATE \"WishlistSources\" SET \"LastSyncedAtUtc\" = now() WHERE \"Name\" = 'Untracked'");
    }

    [PostgresFact]
    public async Task The_name_counts_are_kept_across_a_play_and_a_build_and_recounted_after_a_rename()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var statements = new RecordedStatements();
        await using var db = database.NewContext(null, statements);
        await Seed(db);
        var memo = new DetectionCountMemo();
        var options = Microsoft.Extensions.Options.Options.Create(
            new MusicEnricherOptions { SourceDirectory = "/source", DestinationDirectory = "/dest" });
        async Task<(InboxNameCounts Counts, bool ReadTheLibrary)> Count()
        {
            statements.Sql.Clear();
            var counts = await new InboxNameCounter(
                db,
                new ArtistDuplicateService(db, options, memo, NullLogger<ArtistDuplicateService>.Instance),
                new AlbumDuplicateDetector(db, options, memo),
                new AlbumSplitHealer(db, new AlbumIdentityReconciler(), new DestinationPathResolver(options), options, NullLogger<AlbumSplitHealer>.Instance),
                memo,
                options).CountAsync(Owner.Id);
            return (counts, statements.Sql.Count > 1);
        }

        var first = await Count();
        Assert.True(first.ReadTheLibrary);
        Assert.Equal(1, first.Counts.Artists);

        // A play, a like, a lyrics probe and a build: the lists show these, a name detector reads none.
        await Execute(db, "UPDATE \"Songs\" SET \"PlayCount\" = \"PlayCount\" + 1, \"LastPlayedAtUtc\" = now(), \"LikedAtUtc\" = now()");
        await Execute(db, "UPDATE \"Songs\" SET \"LyricsSyncCheckedAtUtc\" = now(), \"LyricsSyncStatus\" = 1");
        await Execute(db, "UPDATE \"Songs\" SET \"LibraryBuildStatus\" = 3, \"DestinationPath\" = '/dest/a.flac', \"LibraryBuiltAtUtc\" = now()");
        Assert.Equal((first.Counts, false), await Count());

        await Execute(db, "UPDATE \"Songs\" SET \"Artist\" = 'Kanye West', \"AlbumArtist\" = 'Kanye West' WHERE \"Artist\" = 'Kanye Wst'");
        var renamed = await Count();
        Assert.True(renamed.ReadTheLibrary);
        Assert.Equal(0, renamed.Counts.Artists);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Three songs of the owner's (one artist misspelt once), a video, a playlist source, and a friend they share with.</summary>
    private static async Task Seed(MusicHoarderDbContext db)
    {
        db.Users.Add(new User
        {
            Id = Friend.Id,
            Email = Friend.Email,
            EmailNormalized = User.Normalize(Friend.Email),
            DisplayName = "Friend",
            Role = UserRole.Member,
            CreatedAtUtc = DateTime.UtcNow,
        });
        db.LibraryShareGrants.Add(new LibraryShareGrant
        {
            OwnerUserId = Owner.Id,
            GranteeUserId = Friend.Id,
            Scope = ShareGrantScope.Library,
            CreatedAtUtc = DateTime.UtcNow,
        });
        var songs = new[]
        {
            Song("/g/1.flac", "Good Morning", "Graduation", "Kanye West"),
            Song("/g/2.flac", "Champion", "Graduation", "Kanye West"),
            Song("/y/1.flac", "On Sight", "Yeezus", "Kanye Wst"),
        };
        // Lyrics in three states, so a column that only shows beside one of them is exercised.
        songs[0].SyncedLyrics = "[00:01.00]Good morning";
        songs[0].PlainLyrics = "Good morning";
        songs[0].LyricsStatus = LyricsStatus.Fetched;
        songs[1].TranscribedSyncedLyrics = "[00:01.00]Champion";
        songs[1].LyricsStatus = LyricsStatus.NotFound;
        db.Songs.AddRange(songs);
        db.WishlistSources.Add(new WishlistSource
        {
            OwnerUserId = Owner.Id,
            SourceType = WishlistSourceType.Playlist,
            SpotifyPlaylistId = "playlist-1",
            Name = "A playlist",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.SongMusicVideos.Add(new SongMusicVideo
        {
            SongId = songs[0].Id,
            Status = MusicVideoStatus.Ready,
            FilePath = "/videos/1.mp4",
            FetchedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static SongMetadata Song(string sourcePath, string title, string album, string artist) => new()
    {
        OwnerUserId = Owner.Id,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        OriginalMetadataCaptured = true,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        Title = title,
        Year = 2010,
        LibraryBuildStatus = LibraryBuildStatus.Done,
        DestinationPath = $"/dest/{artist}/{album}/{title}.flac",
    };

    private static async Task<string> Written(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        using var stream = new MemoryStream();
        http.Response.Body = stream;
        await result.ExecuteAsync(http);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A value of the column's type that differs from what the row holds.</summary>
    private static string Different(string column, string type) => type switch
    {
        "boolean" => $"NOT coalesce(\"{column}\", false)",
        "integer" or "bigint" or "smallint" or "double precision" or "real" or "numeric" => $"coalesce(\"{column}\", 0) + 1",
        "uuid" => "gen_random_uuid()",
        "timestamp with time zone" => $"coalesce(\"{column}\", timestamptz '2020-01-01 00:00:00+00') + interval '1 second'",
        _ when type == "text" || type.StartsWith("character varying", StringComparison.Ordinal) => $"coalesce(\"{column}\", '') || 'x'",
        _ => throw new NotSupportedException($"Teach {nameof(Different)} to write a new value to a {type} column ({column})."),
    };

    private static async Task<T> Scalar<T>(MusicHoarderDbContext db, string sql)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static Task Execute(MusicHoarderDbContext db, string sql) => db.Database.ExecuteSqlRawAsync(sql);

    private sealed class RecordedStatements : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
