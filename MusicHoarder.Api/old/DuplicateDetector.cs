// using System.Numerics;
// using Models;
//
// namespace MusicHoarder.Services;
//
// public class DuplicateDetectorService
// {
//     public double ComputeSimilarity(uint[] fp1, uint[] fp2)
//     {
//         int length = Math.Min(fp1.Length, fp2.Length);
//         if (length == 0) return 0;
//
//         long totalBits = 0;
//         long differentBits = 0;
//
//         for (int i = 0; i < length; i++)
//         {
//             totalBits += 32;
//             differentBits += BitOperations.PopCount(fp1[i] ^ fp2[i]);
//         }
//
//         double bitErrorRate = (double)differentBits / totalBits;
//         return 1.0 - bitErrorRate;
//     }
//
//     public uint[] DecodeFingerprint(string fingerprint)
//     {
//         if (string.IsNullOrEmpty(fingerprint))
//         {
//             return Array.Empty<uint>();
//         }
//
//         string base64 = fingerprint.Replace('-', '+').Replace('_', '/');
//         while (base64.Length % 4 != 0)
//         {
//             base64 += "=";
//         }
//
//         byte[] bytes = Convert.FromBase64String(base64);
//         var result = new uint[bytes.Length / 4];
//         for (int i = 0; i < result.Length; i++)
//         {
//             result[i] = BitConverter.ToUInt32(bytes, i * 4);
//         }
//
//         return result;
//     }
//
//     public Task<List<DuplicateGroup>> FindDuplicatesAsync(List<SongMetadata> songs, double threshold = 0.70)
//     {
//         var groups = new List<DuplicateGroup>();
//         var processed = new HashSet<string>();
//
//         var songsWithFingerprints = songs.Where(s => !string.IsNullOrEmpty(s.Fingerprint)).ToList();
//
//         for (int i = 0; i < songsWithFingerprints.Count; i++)
//         {
//             var song1 = songsWithFingerprints[i];
//             if (processed.Contains(song1.FilePath)) continue;
//
//             var similarSongs = new List<SongMetadata> { song1 };
//
//             for (int j = i + 1; j < songsWithFingerprints.Count; j++)
//             {
//                 var song2 = songsWithFingerprints[j];
//                 if (processed.Contains(song2.FilePath)) continue;
//
//                 var similarity = ComputeSimilarity(
//                     DecodeFingerprint(song1.Fingerprint!),
//                     DecodeFingerprint(song2.Fingerprint!)
//                 );
//
//                 if (similarity >= threshold)
//                 {
//                     similarSongs.Add(song2);
//                     processed.Add(song2.FilePath);
//                 }
//             }
//
//             if (similarSongs.Count > 1)
//             {
//                 var avgSimilarity = CalculateAverageSimilarity(similarSongs);
//
//                 var category = GetSimilarityCategory(avgSimilarity);
//                 groups.Add(new DuplicateGroup(category, avgSimilarity, similarSongs));
//             }
//
//             processed.Add(song1.FilePath);
//         }
//
//         return Task.FromResult(groups.OrderByDescending(g => g.Score).ToList());
//     }
//
//     private double CalculateAverageSimilarity(List<SongMetadata> songs)
//     {
//         if (songs.Count < 2) return 1.0;
//
//         double totalSimilarity = 0;
//         int comparisons = 0;
//
//         for (int i = 0; i < songs.Count; i++)
//         {
//             for (int j = i + 1; j < songs.Count; j++)
//             {
//                 var sim = ComputeSimilarity(
//                     DecodeFingerprint(songs[i].Fingerprint!),
//                     DecodeFingerprint(songs[j].Fingerprint!)
//                 );
//                 totalSimilarity += sim;
//                 comparisons++;
//             }
//         }
//
//         return comparisons > 0 ? totalSimilarity / comparisons : 1.0;
//     }
//
//     private string GetSimilarityCategory(double score)
//     {
//         if (score > 0.90) return "Near-identical";
//         if (score > 0.70) return "High similarity";
//         if (score > 0.50) return "Possible related";
//         return "Unrelated";
//     }
// }