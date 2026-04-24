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
        try
        {
            if (!fileSystem.File.Exists(path)) return;
            fileSystem.File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Parent directory already pruned (parallel delete); file is effectively gone.
            return;
        }

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

            try
            {
                if (fileSystem.Directory.EnumerateFiles(current).Any()
                    || fileSystem.Directory.EnumerateDirectories(current).Any())
                {
                    break;
                }

                fileSystem.Directory.Delete(current);
            }
            catch (DirectoryNotFoundException)
            {
                // Raced with another delete or external change; nothing left to prune on this branch.
                break;
            }
            catch (IOException)
            {
                // Directory not empty (NAS eventual consistency) or transient IO error; stop pruning.
                break;
            }

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
