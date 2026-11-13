using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicHoarder.Api.Quality;

/// <summary>
/// The stable hash of a grading dossier that a grade stores as its <c>InputFingerprint</c>. One
/// implementation for the song grader, the album grader and the grader trial, which pairs arms on it:
/// two copies that drifted would pair nothing.
/// </summary>
public static class DossierFingerprint
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    public static string Of<T>(T dossier)
    {
        var json = JsonSerializer.Serialize(dossier, CompactJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
