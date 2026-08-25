using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// The latest-grade-per-subject election ("no newer grade exists"), shared by every surface that
/// reads grades. A song or album is regraded over time; only the newest grade is its current
/// verdict, so any query that joins or aggregates grades must elect per subject first — one
/// implementation here keeps the election identical everywhere it happens.
/// </summary>
public static class LatestGradeQueries
{
    /// <summary>Latest grade per song (correlated subquery, translatable and InMemory-test friendly).</summary>
    public static IQueryable<SongQualityGrade> LatestPerSong(this IQueryable<SongQualityGrade> grades) =>
        grades.Where(g => !grades.Any(g2 => g2.SongId == g.SongId && g2.GradedAtUtc > g.GradedAtUtc));

    /// <summary>Latest grade per canonical album (correlated subquery, translatable and InMemory-test friendly).</summary>
    public static IQueryable<CanonicalAlbumQualityGrade> LatestPerAlbum(this IQueryable<CanonicalAlbumQualityGrade> grades) =>
        grades.Where(g => !grades.Any(g2 => g2.CanonicalAlbumId == g.CanonicalAlbumId && g2.GradedAtUtc > g.GradedAtUtc));
}
