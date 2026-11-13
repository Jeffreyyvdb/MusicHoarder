using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace MusicHoarder.Api.Persistence;

/// <summary>
/// The song columns one read-only pass over the library reads, named once beside the code that
/// reads them (see <see cref="SongMetadataLightQuery.WithOnly"/>).
///
/// <para>
/// It is an include list, so the usual default is inverted: a column that is <b>not</b> named is
/// not read, and comes back as the value a blank <see cref="SongMetadata"/> has (null, false, the
/// property's initializer). Code that starts reading another property of these rows must add it
/// here, or it silently works with that blank.
/// </para>
///
/// <para>
/// The one list does three things, so they cannot disagree about what "these columns" are: it is
/// the SQL projection, it makes in-memory copies that carry nothing else
/// (<see cref="Copy(SongMetadata)"/>), and it feeds exactly those values to a
/// <see cref="ContentStamp"/> (<see cref="AddTo(ContentStamp, SongMetadata)"/>). That is what
/// lets a result computed from such copies be remembered under such a stamp: there is nothing on
/// the rows for the computation to read that the stamp did not cover.
/// </para>
/// </summary>
public sealed class SongColumnSet
{
    private readonly PropertyInfo[] _properties;
    private readonly Expression<Func<SongMetadata, SongMetadata>> _projection;
    private bool _mappingChecked;
    private Func<SongMetadata, SongMetadata>? _copy;
    private Action<ContentStamp, SongMetadata>? _addTo;

    /// <param name="names">Property names of <see cref="SongMetadata"/>; use <c>nameof</c>.</param>
    public SongColumnSet(params string[] names)
    {
        // Ordinal order, so two sets naming the same columns are the same set however they were written.
        _properties = names
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => typeof(SongMetadata).GetProperty(name) is { SetMethod.IsPublic: true } property
                ? property
                : throw new ArgumentException($"SongMetadata has no settable property '{name}'.", nameof(names)))
            .ToArray();
        Names = _properties.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var song = Expression.Parameter(typeof(SongMetadata), "s");
        var bindings = _properties
            .Select(p => (MemberBinding)Expression.Bind(p, Expression.Property(song, p)));
        _projection = Expression.Lambda<Func<SongMetadata, SongMetadata>>(
            Expression.MemberInit(Expression.New(typeof(SongMetadata)), bindings), song);
    }

    public IReadOnlySet<string> Names { get; }

    /// <summary>Every column of every given set, for one read that serves several passes.</summary>
    public static SongColumnSet Union(params SongColumnSet[] sets) =>
        new([.. sets.SelectMany(set => set.Names)]);

    /// <summary>These columns and the named ones.</summary>
    public SongColumnSet With(params string[] names) => new([.. Names, .. names]);

    /// <summary><c>s => new SongMetadata { A = s.A, … }</c> over exactly <see cref="Names"/>.</summary>
    internal Expression<Func<SongMetadata, SongMetadata>> Projection(DbContext db)
    {
        if (_mappingChecked)
            return _projection;

        var entity = db.Model.FindEntityType(typeof(SongMetadata))
            ?? throw new InvalidOperationException("SongMetadata is not part of the model.");
        // A property the model does not map cannot be selected as a column: EF would fall back to
        // loading the whole row to compute it, which is the very read this exists to avoid.
        if (_properties.FirstOrDefault(p => entity.FindProperty(p.Name) is null) is { } unmapped)
            throw new InvalidOperationException($"SongMetadata.{unmapped.Name} is not a mapped column.");

        _mappingChecked = true;
        return _projection;
    }

    /// <summary>
    /// A detached song carrying these columns of <paramref name="song"/> and nothing else — what
    /// <see cref="SongMetadataLightQuery.WithOnly"/> would have loaded for it. For handing rows of a
    /// wider read to a pass that must not see the extra columns.
    /// </summary>
    public SongMetadata Copy(SongMetadata song) =>
        LazyInitializer.EnsureInitialized(ref _copy, () => _projection.Compile())(song);

    /// <summary>Feeds these columns of <paramref name="song"/>, in name order, to <paramref name="stamp"/>.</summary>
    public void AddTo(ContentStamp stamp, SongMetadata song) =>
        LazyInitializer.EnsureInitialized(ref _addTo, BuildAddTo)(stamp, song);

    /// <summary>Feeds how many songs there are, then these columns of each in the order given.</summary>
    public void AddTo(ContentStamp stamp, IReadOnlyList<SongMetadata> songs)
    {
        var addTo = LazyInitializer.EnsureInitialized(ref _addTo, BuildAddTo);
        stamp.Add(songs.Count);
        foreach (var song in songs)
            addTo(stamp, song);
    }

    /// <summary><see cref="Copy(SongMetadata)"/> of each song, in the order given.</summary>
    public List<SongMetadata> Copy(IReadOnlyList<SongMetadata> songs)
    {
        var copy = LazyInitializer.EnsureInitialized(ref _copy, () => _projection.Compile());
        var copies = new List<SongMetadata>(songs.Count);
        foreach (var song in songs)
            copies.Add(copy(song));
        return copies;
    }

    private Action<ContentStamp, SongMetadata> BuildAddTo()
    {
        var stamp = Expression.Parameter(typeof(ContentStamp), "stamp");
        var song = Expression.Parameter(typeof(SongMetadata), "s");
        var adds = _properties.Select(p => Add(stamp, Expression.Property(song, p)));
        return Expression.Lambda<Action<ContentStamp, SongMetadata>>(
            Expression.Block(adds.DefaultIfEmpty(Expression.Empty())), stamp, song).Compile();
    }

    // stamp.Add(value), through the overload for the column's type.
    private static Expression Add(ParameterExpression stamp, Expression value)
    {
        if (Nullable.GetUnderlyingType(value.Type) is not null)
        {
            // Whether there is a value first, so a null and a zero are different bytes.
            var hasValue = Expression.Property(value, nameof(Nullable<int>.HasValue));
            return Expression.Block(
                Add(stamp, hasValue),
                Expression.IfThen(hasValue, Add(stamp, Expression.Property(value, nameof(Nullable<int>.Value)))));
        }

        if (value.Type.IsEnum)
            value = Expression.Convert(Expression.Convert(value, Enum.GetUnderlyingType(value.Type)), typeof(long));
        else if (value.Type == typeof(DateTime))
            value = Expression.Call(value, nameof(DateTime.ToBinary), Type.EmptyTypes);

        var add = typeof(ContentStamp).GetMethod(nameof(ContentStamp.Add), [value.Type])
            ?? throw new NotSupportedException($"{nameof(ContentStamp)} cannot add a {value.Type.Name}.");
        return Expression.Call(stamp, add, value);
    }
}
