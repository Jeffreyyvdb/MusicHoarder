namespace MusicHoarder.Api.Download;

/// <summary>How much room a drive has, behind a seam so a test can say how much.</summary>
public interface IFreeDiskSpace
{
    /// <summary>
    /// Bytes free on the drive that holds <paramref name="directory"/>, even when the directory does
    /// not exist yet (its nearest existing parent is asked). Null when that cannot be told.
    /// </summary>
    long? AvailableBytes(string directory);
}

public sealed class DriveFreeDiskSpace : IFreeDiskSpace
{
    public long? AvailableBytes(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        try
        {
            var existing = Path.GetFullPath(directory);
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (parent is null)
                    return null;
                existing = parent;
            }
            return new DriveInfo(existing).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
