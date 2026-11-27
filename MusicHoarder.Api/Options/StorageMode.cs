namespace MusicHoarder.Api.Options;

/// <summary>How the instance stores audio. See <see cref="MusicEnricherOptions.StorageMode"/>.</summary>
public enum StorageMode
{
    /// <summary>A source share, a download folder and a tagged destination tree for a media server.</summary>
    Folders = 0,

    /// <summary>Content-addressed audio assets, played from where they are stored, with tags kept in the database.</summary>
    Hosted = 1,
}
