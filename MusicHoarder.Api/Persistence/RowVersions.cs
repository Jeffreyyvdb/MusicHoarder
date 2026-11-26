using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// Version numbers Postgres keeps on the rows the library lists and the Inbox's name counts are
/// stamped by, so those can tell a write that changes what they show from one that does not.
/// <para>
/// A stamp that hashes <c>xmin</c> moves on every write. In production the wishlist sync stamps its
/// sources every two minutes and the lyrics timing check stamps each song it probes, so the lists
/// never once answered "unchanged". A version column moves only when a trigger says the write
/// mattered: it draws the next value of one sequence on every insert, and on every update
/// <b>except</b> one that changes nothing but the columns its <see cref="RowVersion.Ignored"/> list
/// names.
/// </para>
/// <para>
/// <b>The lists are ignore lists, on purpose.</b> A column nobody thought about counts as visible,
/// so a new one costs at worst a download that was not needed. It can never leave a client holding
/// an out-of-date library, which is the one failure the stamps must not have. Put a column on a
/// list only when the reader of that version provably does not read it;
/// <c>RowVersionTests</c> fails when a list names a column the lists' SQL or the name detectors'
/// column set contains.
/// </para>
/// <para>
/// The comparison is over <c>to_jsonb</c> of the old and new row with the ignored keys removed, so
/// the function never names a column outside its lists: a column that is renamed or dropped later
/// stops being ignored instead of breaking every write to the table. A value never comes back,
/// because it is a sequence's: a change that is later undone still leaves a new version behind.
/// </para>
/// <para>
/// <b>What a database runs is what its newest migration installed</b>, not this file. After
/// changing a list, add a migration whose <c>Up</c> runs <see cref="RowVersionTable.InstallSql"/>
/// pasted as a literal (and whose <c>Down</c> runs the previous one); the same test compares each
/// installed function with what this file generates and prints the SQL to paste.
/// </para>
/// </summary>
public static class RowVersions
{
    /// <summary>One sequence for every version column, so a value is never issued twice.</summary>
    public const string Sequence = "RowVersions";

    /// <summary>What <c>GET /songs</c> and <c>GET /api/albums</c> are stamped by.</summary>
    public const string List = "ListVersion";

    /// <summary>What the Inbox's name counts are stamped by; on <c>Songs</c> only.</summary>
    public const string Names = "NameVersion";

    /// <summary>
    /// Attempt counters and "last tried" stamps the pipeline's sweeps write on a song, which no
    /// list row carries.
    /// </summary>
    private static readonly string[] SongBookkeeping =
    [
        // The lyrics timing check, in batches, around the clock.
        nameof(SongMetadata.LyricsSyncCheckedAtUtc),
        nameof(SongMetadata.LyricsSyncConfidence),
        nameof(SongMetadata.LyricsSyncProbeAttempts),
        // The lyrics fetch and its recheck sweep.
        nameof(SongMetadata.LyricsLastAttemptedAtUtc),
        nameof(SongMetadata.LyricsFetchAttempts),
        nameof(SongMetadata.LyricsNextRecheckAfterUtc),
        // The library builder, beside the status it sets.
        nameof(SongMetadata.LibraryBuildLastAttemptedAtUtc),
        nameof(SongMetadata.LibraryBuildAttempts),
        nameof(SongMetadata.LastWrittenAtUtc),
        nameof(SongMetadata.LastWrittenTagsJson),
        nameof(SongMetadata.PreviousDestinationPath),
        // The enrichment retry sweep, on a retry that found nothing new.
        nameof(SongMetadata.EnrichmentLastAttemptedAtUtc),
        nameof(SongMetadata.EnrichedAtUtc),
    ];

    /// <summary>
    /// What a play, a like, a lyrics fetch, a transcription and a build write. The lists show all of
    /// it; a name detector reads none of it, and a play must not make the Inbox recount.
    /// </summary>
    private static readonly string[] SongNotAName =
    [
        nameof(SongMetadata.PlayCount),
        nameof(SongMetadata.LastPlayedAtUtc),
        nameof(SongMetadata.LikedAtUtc),
        nameof(SongMetadata.LikeLastSyncedValue),
        nameof(SongMetadata.PlainLyrics),
        nameof(SongMetadata.SyncedLyrics),
        nameof(SongMetadata.HasPlainLyrics),
        nameof(SongMetadata.HasSyncedLyrics),
        nameof(SongMetadata.HasNonBlankPlainLyrics),
        nameof(SongMetadata.HasNonBlankSyncedLyrics),
        nameof(SongMetadata.LyricsStatus),
        nameof(SongMetadata.LyricsOrigin),
        nameof(SongMetadata.LyricsTextChangedAtUtc),
        nameof(SongMetadata.LyricsSyncStatus),
        nameof(SongMetadata.LyricsSyncIssue),
        nameof(SongMetadata.LyricsSyncOffsetMs),
        nameof(SongMetadata.TranscribedSyncedLyrics),
        nameof(SongMetadata.TranscribedPlainLyrics),
        nameof(SongMetadata.HasTranscribedLyrics),
        nameof(SongMetadata.TranscriptionStatus),
        nameof(SongMetadata.TranscribedAtUtc),
        nameof(SongMetadata.TranscriptionWordsStampUtc),
        nameof(SongMetadata.TranscriptionError),
        nameof(SongMetadata.LibraryBuildStatus),
        nameof(SongMetadata.LibraryBuiltAtUtc),
        nameof(SongMetadata.LibraryBuildError),
        nameof(SongMetadata.DestinationPath),
        nameof(SongMetadata.HasCoverArt),
        nameof(SongMetadata.CoverUpdatedAtUtc),
    ];

    public static readonly RowVersionTable Songs = new(
        typeof(SongMetadata),
        "Songs",
        new RowVersion(List, SongBookkeeping),
        new RowVersion(Names, [.. SongBookkeeping, .. SongNotAName]));

    /// <summary>The lists read a video's song, status and file path: whether there is one to play.</summary>
    public static readonly RowVersionTable SongMusicVideos = new(
        typeof(SongMusicVideo),
        "SongMusicVideos",
        new RowVersion(List,
        [
            // What measuring a fetched file writes, and a failed fetch's message.
            nameof(SongMusicVideo.LetterboxFraction),
            nameof(SongMusicVideo.PillarboxFraction),
            nameof(SongMusicVideo.SyncOffsetMs),
            nameof(SongMusicVideo.SyncSource),
            nameof(SongMusicVideo.SyncConfidence),
            nameof(SongMusicVideo.LastError),
        ]));

    /// <summary>The lists read a source's id, type and name, for a song's origin.</summary>
    public static readonly RowVersionTable WishlistSources = new(
        typeof(WishlistSource),
        "WishlistSources",
        new RowVersion(List,
        [
            // The wishlist sync, on every pass over every source.
            nameof(WishlistSource.LastSyncedAtUtc),
            nameof(WishlistSource.RemoteChecksum),
            nameof(WishlistSource.TracksRecordedAtUtc),
        ]));

    public static readonly IReadOnlyList<RowVersionTable> Tables = [Songs, SongMusicVideos, WishlistSources];

    /// <summary>Maps the sequence and every version column. They are shadow properties: nothing in the app sets one.</summary>
    public static void Map(ModelBuilder modelBuilder)
    {
        modelBuilder.HasSequence<long>(Sequence);
        foreach (var table in Tables)
        {
            foreach (var version in table.Versions)
                modelBuilder.Entity(table.Entity).Property<long?>(version.Column);
        }
    }

    /// <summary>
    /// What a table adds to a stamp: how many rows, and a sum over each row's key and
    /// <paramref name="version"/>. The count catches a row arriving or leaving; the sum moves when
    /// any row's version does. A sum rather than an xor, so a row that a join happened to repeat
    /// could not cancel itself.
    /// </summary>
    /// <param name="key">The primary key column, already delimited.</param>
    /// <param name="version"><see cref="Kept"/> or <see cref="Physical"/>.</param>
    public static string FoldSql(string alias, string key, string version) =>
        $"count(*)::text || ':' || coalesce(sum(hashtextextended({alias}.{key}::text || '/' || {version}, 0)), 0)::text";

    /// <summary>
    /// The version Postgres itself keeps: <c>xmin</c> (the transaction that wrote this version of
    /// the row) and <c>ctid</c> (where it lives). It moves on every write, whatever the write changed.
    /// </summary>
    public static string Physical(string alias) => $"{alias}.xmin::text || '/' || {alias}.ctid::text";

    /// <summary>
    /// The version a trigger keeps in <paramref name="versionColumn"/>. A row with none was written
    /// while its table had no trigger, so nothing vouches for it and it falls back to
    /// <see cref="Physical"/>. That costs downloads and never serves a stale answer.
    /// </summary>
    public static string Kept(string alias, string versionColumn) =>
        $"coalesce({alias}.\"{versionColumn}\"::text, {Physical(alias)})";
}

/// <param name="Column">The version column.</param>
/// <param name="Ignored">Property names; a write that changes only these leaves the version alone.</param>
public sealed record RowVersion(string Column, IReadOnlyList<string> Ignored);

public sealed class RowVersionTable(Type entity, string table, params RowVersion[] versions)
{
    public Type Entity { get; } = entity;
    public string Table { get; } = table;
    public IReadOnlyList<RowVersion> Versions { get; } = versions;

    /// <summary>The function and the trigger carry one name, after their table.</summary>
    public string TriggerName => $"{Table}_RowVersions";

    public RowVersion Version(string column) => Versions.Single(v => v.Column == column);

    /// <summary>
    /// The trigger function's body. The newest migration must have installed exactly this; see
    /// <see cref="RowVersions"/>.
    /// </summary>
    public string FunctionBody()
    {
        var body = new StringBuilder();
        body.Append("\nDECLARE\n  was jsonb;\n  becomes jsonb;\n");
        foreach (var version in Versions)
        {
            var ignored = string.Join(", ", version.Ignored.Order(StringComparer.Ordinal).Select(name => $"'{name}'"));
            body.Append($"  \"{version.Column}Ignores\" CONSTANT text[] := ARRAY[{ignored}]::text[];\n");
        }
        body.Append("BEGIN\n  IF TG_OP = 'INSERT' THEN\n");
        foreach (var version in Versions)
            body.Append($"    NEW.\"{version.Column}\" := nextval('\"{RowVersions.Sequence}\"');\n");
        body.Append("    RETURN NEW;\n  END IF;\n");
        // Whatever the statement set them to: only this function moves a version.
        foreach (var version in Versions)
            body.Append($"  NEW.\"{version.Column}\" := OLD.\"{version.Column}\";\n");
        body.Append("  was := to_jsonb(OLD);\n  becomes := to_jsonb(NEW);\n");
        foreach (var version in Versions)
        {
            body.Append($"  IF (becomes - \"{version.Column}Ignores\") IS DISTINCT FROM (was - \"{version.Column}Ignores\") THEN\n");
            body.Append($"    NEW.\"{version.Column}\" := nextval('\"{RowVersions.Sequence}\"');\n  END IF;\n");
        }
        body.Append("  RETURN NEW;\nEND\n");
        return body.ToString();
    }

    /// <summary>Creates or replaces the function and its trigger. Safe to run again.</summary>
    public string InstallSql() =>
        $"""
        CREATE OR REPLACE FUNCTION "{TriggerName}"() RETURNS trigger LANGUAGE plpgsql AS $rowversions${FunctionBody()}$rowversions$;
        DROP TRIGGER IF EXISTS "{TriggerName}" ON "{Table}";
        CREATE TRIGGER "{TriggerName}" BEFORE INSERT OR UPDATE ON "{Table}"
          FOR EACH ROW EXECUTE FUNCTION "{TriggerName}"();
        """;

    /// <summary>The ignored properties' column names, as the trigger must spell them.</summary>
    public IEnumerable<string> IgnoredColumns(IModel model, RowVersion version)
    {
        var entityType = model.FindEntityType(Entity)!;
        var storeObject = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)!.Value;
        return version.Ignored.Select(name => entityType.FindProperty(name)?.GetColumnName(storeObject)
            ?? throw new InvalidOperationException($"{Entity.Name}.{name} is not a mapped column."));
    }
}
