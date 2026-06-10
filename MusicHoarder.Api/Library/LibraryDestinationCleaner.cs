using System.IO.Abstractions;

namespace MusicHoarder.Api.Library;

public interface ILibraryDestinationCleaner
{
    void DeleteManagedPathAndPrune(string path, string destinationRoot);
}

public class LibraryDestinationCleaner(
    IFileSystem fileSystem,
    ILogger<LibraryDestinationCleaner>? logger = null) : ILibraryDestinationCleaner
{
    private static readonly string[] CoverImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];

    public void DeleteManagedPathAndPrune(string path, string destinationRoot)
    {
        // Hard guard: this only ever deletes files the library builder *manages* (under the
        // destination root). A path outside it is a source file (the non-destructive invariant) or a
        // bug upstream — never delete it. Without this, a polluted PreviousDestinationPath (e.g. a demo
        // row whose DestinationPath == SourcePath) would delete the source.
        if (!IsWithinRoot(path, fileSystem.Path.GetFullPath(destinationRoot)))
        {
            logger?.LogWarning(
                "Refusing to delete {Path}: outside the managed destination root {Root}. This path is not library-managed.",
                path, destinationRoot);
            return;
        }

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
                if (fileSystem.Directory.EnumerateDirectories(current).Any())
                {
                    break;
                }

                var files = fileSystem.Directory.EnumerateFiles(current).ToList();
                if (files.Count > 0)
                {
                    // The album-cover pass may have left a cover.* behind in this otherwise-empty
                    // managed folder (e.g. after a track moved albums). Remove that — and only that —
                    // so the folder can be pruned; any other file means the folder is still in use.
                    if (!files.All(IsManagedCoverFile))
                    {
                        break;
                    }

                    foreach (var coverFile in files)
                    {
                        fileSystem.File.Delete(coverFile);
                    }
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

    private bool IsManagedCoverFile(string path)
    {
        var name = fileSystem.Path.GetFileNameWithoutExtension(path);
        var ext = fileSystem.Path.GetExtension(path);
        return string.Equals(name, "cover", StringComparison.OrdinalIgnoreCase)
            && CoverImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private bool IsWithinRoot(string path, string rootFullPath)
    {
        var fullPath = fileSystem.Path.GetFullPath(path);
        return fullPath.StartsWith(rootFullPath, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, StringComparison.Ordinal);
}
