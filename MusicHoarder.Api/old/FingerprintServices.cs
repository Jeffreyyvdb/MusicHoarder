// using System.Diagnostics;
// using System.Text.Json;
// using Models;
//
// namespace Services;
//
// public class FingerprintService
// {
//     private readonly string _fpcalcPath = "fpcalc";
//
//     public FingerprintService(string fpcalcPath = "fpcalc")
//     {
//         _fpcalcPath = fpcalcPath ?? throw new ArgumentNullException(nameof(fpcalcPath));
//     }
//
//     public async Task<FingerprintResult?> ComputeFingerprintAsync(string filePath)
//     {
//         if (filePath == null)
//         {
//             throw new ArgumentNullException(nameof(filePath));
//         }
//
//         if (string.IsNullOrEmpty(filePath))
//         {
//             return null;
//         }
//
//         try
//         {
//             var psi = new ProcessStartInfo(_fpcalcPath, $"-json \"{filePath}\"")
//             {
//                 RedirectStandardOutput = true,
//                 UseShellExecute = false,
//                 CreateNoWindow = true
//             };
//
//             using var process = Process.Start(psi);
//             if (process == null)
//             {
//                 Console.WriteLine($"Warning: Failed to start {_fpcalcPath} process");
//                 return null;
//             }
//
//             string output = await process.StandardOutput.ReadToEndAsync();
//             await process.WaitForExitAsync();
//
//             if (process.ExitCode != 0)
//             {
//                 Console.WriteLine($"Warning: {_fpcalcPath} exited with code {process.ExitCode}");
//                 return null;
//             }
//
//             var doc = JsonDocument.Parse(output);
//             var duration = doc.RootElement.GetProperty("duration").GetDouble();
//             var fingerprint = doc.RootElement.GetProperty("fingerprint").GetString();
//
//             if (string.IsNullOrEmpty(fingerprint))
//             {
//                 Console.WriteLine($"Warning: Empty fingerprint returned by {_fpcalcPath}");
//                 return null;
//             }
//
//             return new FingerprintResult((int)duration, fingerprint);
//         }
//         catch (Exception ex)
//         {
//             Console.WriteLine($"Warning: Error computing fingerprint for {filePath}: {ex.Message}");
//             return null;
//         }
//     }
// }