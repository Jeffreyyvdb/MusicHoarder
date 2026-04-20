using System.IO.Abstractions;

namespace MusicHoarder.Api.Library;

public interface ILibraryDestinationCleaner
{
    void DeleteManagedPathAndPrune(string path, string destinationRoot);
}

public class LibraryDestinationCleaner(IFileSystem fileSystem) : ILibraryDestinationCleaner
{
    public void DeleteManagedPathAndPrune(string path, string destinationRoot)
    {
        if (!fileSystem.File.Exists(path))
        {
            return;
        }

        fileSystem.File.Delete(path);
        PruneEmptyDirectories(fileSystem.Path.GetDirectoryName(path), destinationRoot);
    }

    private void PruneEmptyDirectories(string? startDirectory, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return;
        }

        var current = startDirectory;
        var rootFullPath = fileSystem.Path.GetFullPath(destinationRoot);
        while (!string.IsNullOrWhiteSpace(current) && IsWithinRoot(current, rootFullPath))
        {
            if (!fileSystem.Directory.Exists(current))
            {
                break;
            }

            var hasFiles = fileSystem.Directory.EnumerateFiles(current).Any();
            var hasDirectories = fileSystem.Directory.EnumerateDirectories(current).Any();
            if (hasFiles || hasDirectories)
            {
                break;
            }

            fileSystem.Directory.Delete(current);
            if (PathsEqual(current, rootFullPath))
            {
                break;
            }

            current = fileSystem.Path.GetDirectoryName(current);
        }
    }

    private bool IsWithinRoot(string path, string rootFullPath)
    {
        var fullPath = fileSystem.Path.GetFullPath(path);
        return fullPath.StartsWith(rootFullPath, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, StringComparison.Ordinal);
}
