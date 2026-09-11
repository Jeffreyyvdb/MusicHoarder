namespace MusicHoarder.Api.Storage;

/// <summary>Capacity of the filesystem a directory lives on.</summary>
public sealed record VolumeCapacity(long TotalBytes, long FreeBytes);

/// <summary>Asks the OS how big the volume behind a directory is. Null when it cannot say.</summary>
public interface IStorageVolumeProbe
{
    VolumeCapacity? Probe(string directoryPath);
}

/// <summary>
/// <see cref="DriveInfo"/> over the directory itself. On Linux/macOS that is a statvfs on the path,
/// which works for any existing directory — including a Docker bind mount, where it reports the host
/// filesystem behind it. Windows only accepts a drive root, so the path is reduced to one there.
/// </summary>
public sealed class DriveInfoVolumeProbe : IStorageVolumeProbe
{
    public VolumeCapacity? Probe(string directoryPath)
    {
        try
        {
            var name = OperatingSystem.IsWindows()
                ? Path.GetPathRoot(Path.GetFullPath(directoryPath)) ?? directoryPath
                : directoryPath;
            var drive = new DriveInfo(name);
            return new VolumeCapacity(drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
