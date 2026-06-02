namespace MusicHoarder.Api.Quality;

/// <summary>
/// Progress snapshot for album grading. A distinct DI singleton from the song
/// <see cref="QualityGradingProgressTracker"/> (so the two runs don't collide) reusing all its logic.
/// </summary>
public sealed class AlbumGradingProgressTracker : QualityGradingProgressTracker;
