using System.Reflection;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Persistence;

/// <summary>
/// Gives a song's columns values that are not their defaults, by reflection over the EF model, so a
/// test can tell a column that was read from one that was left out — including the columns added
/// after the test was written.
/// </summary>
internal static class SongColumnFill
{
    /// <summary>Every column of a song a projection can carry: mapped by the model, settable by code.</summary>
    public static IReadOnlyList<PropertyInfo> Mapped(DbContext db) =>
        db.Model.FindEntityType(typeof(SongMetadata))!.GetProperties()
            .Select(p => p.PropertyInfo)
            .Where(p => p is { SetMethod.IsPublic: true })
            .Select(p => p!)
            .ToList();

    /// <summary>A song as a projection that reads nothing leaves it: only what the type initializes.</summary>
    public static SongMetadata Blank() => (SongMetadata)Activator.CreateInstance(typeof(SongMetadata))!;

    /// <summary>
    /// Sets every column not named in <paramref name="keep"/> to a value that differs from its
    /// default and, through <paramref name="seed"/>, from the same column of another song. The key
    /// is always left for the database to assign.
    /// </summary>
    public static SongMetadata FillExcept(this SongMetadata song, DbContext db, int seed, params IEnumerable<string>[] keep)
    {
        var kept = keep.SelectMany(names => names).Append(nameof(SongMetadata.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var property in Mapped(db).Where(p => !kept.Contains(p.Name)))
            property.SetValue(song, ValueFor(property, seed));
        return song;
    }

    private static object ValueFor(PropertyInfo property, int seed)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(string)) return $"{property.Name} {seed}";
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return 1000 + seed;
        if (type == typeof(long)) return 1000L + seed;
        if (type == typeof(double)) return 1000.5 + seed;
        if (type == typeof(Guid)) return new Guid(seed + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        if (type == typeof(DateTime)) return new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(seed);
        // The last member, never the first: the first is the default every enum column starts at.
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().Last();
        throw new NotSupportedException(
            $"SongMetadata.{property.Name} is a {type.Name}; teach {nameof(SongColumnFill)} a value for it.");
    }
}
