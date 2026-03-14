// using System;
// using System.IO;
// using Models;
//
// namespace Services;
//
// public class MetadataExtractor
// {
//     public SongMetadata ExtractMetadata(string filePath, long lastModified)
//     {
//         var fileName = Path.GetFileName(filePath);
//         var extension = Path.GetExtension(filePath).ToLowerInvariant();
//         long fileSize = 0;
//
//         try
//         {
//             TagLib.File? tagFile = null;
//
//             // Read from actual file
//             try
//             {
//                 tagFile = TagLib.File.Create(filePath);
//             }
//             catch
//             {
//                 // Can't read file
//             }
//
//             // Get file size with error handling
//             try
//             {
//                 fileSize = new FileInfo(filePath).Length;
//             }
//             catch
//             {
//                 fileSize = 0;
//             }
//
//             string? artist = null;
//             string? album = null;
//             string? title = null;
//             int? year = null;
//             int? trackNumber = null;
//
//             if (tagFile?.Tag != null)
//             {
//                 var tag = tagFile.Tag;
//                 album = tag.Album;
//                 artist = tag.AlbumArtists?.Length > 0 ? tag.AlbumArtists[0] : tag.FirstPerformer;
//                 title = tag.Title;
//                 year = tag.Year != 0 ? (int)tag.Year : null;
//                 trackNumber = tag.Track != 0 ? (int)tag.Track : null;
//             }
//
//             return new SongMetadata
//             {
//                 FilePath = filePath,
//                 FileName = fileName,
//                 FileSize = fileSize,
//                 Extension = extension,
//                 LastModified = lastModified,
//                 Artist = artist,
//                 Album = album,
//                 Title = title,
//                 Year = year,
//                 TrackNumber = trackNumber,
//                 IndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
//             };
//         }
//         catch (Exception ex)
//         {
//             // Fallback to minimal metadata on error
//             Console.WriteLine($"Warning: Error reading metadata from {filePath}: {ex.Message}");
//
//             long fallbackFileSize = 0;
//             try
//             {
//                 fallbackFileSize = new FileInfo(filePath).Length;
//             }
//             catch
//             {
//                 fallbackFileSize = 0;
//             }
//
//             return new SongMetadata
//             {
//                 FilePath = filePath,
//                 FileName = fileName,
//                 FileSize = fallbackFileSize,
//                 Extension = extension,
//                 LastModified = lastModified,
//                 Artist = null,
//                 Album = null,
//                 Title = null,
//                 Year = null,
//                 TrackNumber = null,
//                 IndexedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
//             };
//         }
//     }
// }