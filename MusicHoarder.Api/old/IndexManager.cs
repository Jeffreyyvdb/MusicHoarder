// using Microsoft.Data.Sqlite;
// using Models;
//
// namespace MusicHoarder.Services
// {
//     public class IndexManager(string databasePath)
//     {
//         public void Initialize()
//         {
//             using var connection = new SqliteConnection($"Data Source={databasePath}");
//             connection.Open();
//
//             var createTableCommand = @"
//                 CREATE TABLE IF NOT EXISTS Songs (
//                     Id INTEGER PRIMARY KEY AUTOINCREMENT,
//                     FilePath TEXT NOT NULL UNIQUE,
//                     FileName TEXT NOT NULL,
//                     FileSize INTEGER NOT NULL,
//                     Extension TEXT NOT NULL,
//                     LastModified INTEGER NOT NULL,
//                     Artist TEXT,
//                     Album TEXT,
//                     Title TEXT,
//                     Year INTEGER,
//                     TrackNumber INTEGER,
//                     IndexedAt INTEGER NOT NULL,
//                     Fingerprint TEXT,
//                     Duration INTEGER
//                 );
//
//                 CREATE INDEX IF NOT EXISTS idx_file_path ON Songs(FilePath);
//             ";
//
//             using var command = connection.CreateCommand();
//             command.CommandText = createTableCommand;
//             command.ExecuteNonQuery();
//
//             var checkColumnsCommand = connection.CreateCommand();
//             checkColumnsCommand.CommandText = "PRAGMA table_info(Songs)";
//             using var reader = checkColumnsCommand.ExecuteReader();
//
//             var columns = new List<string>();
//             while (reader.Read())
//             {
//                 columns.Add(reader.GetString(1));
//             }
//
//             using var addColumnCommand = connection.CreateCommand();
//
//             if (!columns.Contains("Fingerprint"))
//             {
//                 addColumnCommand.CommandText = "ALTER TABLE Songs ADD COLUMN Fingerprint TEXT";
//                 addColumnCommand.ExecuteNonQuery();
//             }
//
//             if (!columns.Contains("Duration"))
//             {
//                 addColumnCommand.CommandText = "ALTER TABLE Songs ADD COLUMN Duration INTEGER";
//                 addColumnCommand.ExecuteNonQuery();
//             }
//         }
//
//         public SongMetadata? GetSongByFilePath(string filePath)
//         {
//             using var connection = new SqliteConnection($"Data Source={databasePath}");
//             connection.Open();
//
//             using var command = connection.CreateCommand();
//             command.CommandText = @"
//                 SELECT FilePath, FileName, FileSize, Extension, LastModified,
//                        Artist, Album, Title, Year, TrackNumber, IndexedAt, Fingerprint, Duration
//                 FROM Songs
//                 WHERE FilePath = @filePath
//             ";
//             command.Parameters.AddWithValue("@filePath", filePath);
//
//             using var reader = command.ExecuteReader();
//             if (reader.Read())
//             {
//                 return MapReaderToSongMetadata(reader);
//             }
//
//             return null;
//         }
//
//         private SongMetadata MapReaderToSongMetadata(SqliteDataReader reader)
//         {
//             return new SongMetadata
//             {
//                 FilePath = reader.GetString(0),
//                 FileName = reader.GetString(1),
//                 FileSize = reader.GetInt64(2),
//                 Extension = reader.GetString(3),
//                 LastModified = reader.GetInt64(4),
//                 Artist = reader.IsDBNull(5) ? null : reader.GetString(5),
//                 Album = reader.IsDBNull(6) ? null : reader.GetString(6),
//                 Title = reader.IsDBNull(7) ? null : reader.GetString(7),
//                 Year = reader.IsDBNull(8) ? null : reader.GetInt32(8),
//                 TrackNumber = reader.IsDBNull(9) ? null : reader.GetInt32(9),
//                 IndexedAt = reader.GetInt64(10),
//                 Fingerprint = reader.IsDBNull(11) ? null : reader.GetString(11),
//                 Duration = reader.IsDBNull(12) ? null : reader.GetInt32(12)
//             };
//         }
//
//         public void InsertSong(SongMetadata metadata)
//         {
//             using var connection = new SqliteConnection($"Data Source={databasePath}");
//             connection.Open();
//
//             using var command = connection.CreateCommand();
//             command.CommandText = @"
//                 INSERT INTO Songs (FilePath, FileName, FileSize, Extension, LastModified,
//                                   Artist, Album, Title, Year, TrackNumber, IndexedAt, Fingerprint, Duration)
//                 VALUES (@filePath, @fileName, @fileSize, @extension, @lastModified,
//                         @artist, @album, @title, @year, @trackNumber, @indexedAt, @fingerprint, @duration)
//             ";
//
//             AddSongParameters(command, metadata);
//             command.ExecuteNonQuery();
//         }
//
//         public void UpdateSong(SongMetadata metadata)
//         {
//             using var connection = new SqliteConnection($"Data Source={databasePath}");
//             connection.Open();
//
//             using var command = connection.CreateCommand();
//             command.CommandText = @"
//                 UPDATE Songs
//                 SET FileName = @fileName,
//                     FileSize = @fileSize,
//                     Extension = @extension,
//                     LastModified = @lastModified,
//                     Artist = @artist,
//                     Album = @album,
//                     Title = @title,
//                     Year = @year,
//                     TrackNumber = @trackNumber,
//                     IndexedAt = @indexedAt,
//                     Fingerprint = @fingerprint,
//                     Duration = @duration
//                 WHERE FilePath = @filePath
//             ";
//
//             AddSongParameters(command, metadata);
//             command.ExecuteNonQuery();
//         }
//
//         private void AddSongParameters(SqliteCommand command, SongMetadata metadata)
//         {
//             command.Parameters.AddWithValue("@filePath", metadata.FilePath);
//             command.Parameters.AddWithValue("@fileName", metadata.FileName);
//             command.Parameters.AddWithValue("@fileSize", metadata.FileSize);
//             command.Parameters.AddWithValue("@extension", metadata.Extension);
//             command.Parameters.AddWithValue("@lastModified", metadata.LastModified);
//             command.Parameters.AddWithValue("@artist", (object?)metadata.Artist ?? DBNull.Value);
//             command.Parameters.AddWithValue("@album", (object?)metadata.Album ?? DBNull.Value);
//             command.Parameters.AddWithValue("@title", (object?)metadata.Title ?? DBNull.Value);
//             command.Parameters.AddWithValue("@year", (object?)metadata.Year ?? DBNull.Value);
//             command.Parameters.AddWithValue("@trackNumber", (object?)metadata.TrackNumber ?? DBNull.Value);
//             command.Parameters.AddWithValue("@indexedAt", metadata.IndexedAt);
//             command.Parameters.AddWithValue("@fingerprint", (object?)metadata.Fingerprint ?? DBNull.Value);
//             command.Parameters.AddWithValue("@duration", (object?)metadata.Duration ?? DBNull.Value);
//         }
//
//         public List<SongMetadata> GetAllSongs()
//         {
//             using var connection = new SqliteConnection($"Data Source={databasePath}");
//             connection.Open();
//
//             using var command = connection.CreateCommand();
//             command.CommandText = @"
//                 SELECT FilePath, FileName, FileSize, Extension, LastModified,
//                        Artist, Album, Title, Year, TrackNumber, IndexedAt, Fingerprint, Duration
//                 FROM Songs
//             ";
//
//             var songs = new List<SongMetadata>();
//             using var reader = command.ExecuteReader();
//             while (reader.Read())
//             {
//                 songs.Add(MapReaderToSongMetadata(reader));
//             }
//
//             return songs;
//         }
//     }
// }