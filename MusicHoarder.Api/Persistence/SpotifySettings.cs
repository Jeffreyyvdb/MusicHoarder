using System.ComponentModel.DataAnnotations;

namespace MusicHoarder.Api.Persistence;

public class SpotifySettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Owner of this row. The pre-migration code treated <c>SpotifySettings</c> as a single row;
    /// now there is one row per user.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public DateTime? TokenExpiresAtUtc { get; set; }

    public DateTime? ConnectedAtUtc { get; set; }

    public bool IsConnected =>
        !string.IsNullOrWhiteSpace(AccessToken)
        && !string.IsNullOrWhiteSpace(RefreshToken);

    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret);

    public bool IsTokenExpiringSoon(TimeSpan buffer) =>
        TokenExpiresAtUtc.HasValue && TokenExpiresAtUtc.Value - DateTime.UtcNow <= buffer;

    public void StoreTokens(string accessToken, string refreshToken, int expiresInSeconds)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        TokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresInSeconds);
        ConnectedAtUtc ??= DateTime.UtcNow;
    }

    public void UpdateAccessToken(string accessToken, int expiresInSeconds, string? newRefreshToken = null)
    {
        AccessToken = accessToken;
        TokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresInSeconds);
        if (!string.IsNullOrWhiteSpace(newRefreshToken))
            RefreshToken = newRefreshToken;
    }

    public void ClearTokens()
    {
        AccessToken = null;
        RefreshToken = null;
        TokenExpiresAtUtc = null;
        ConnectedAtUtc = null;
        SpotifyLikedMatchStatsUpdatedAtUtc = null;
        SpotifyLikedMatchTotal = null;
        SpotifyLikedMatchInLibrary = null;
        SpotifyLikedMatchPossible = null;
        SpotifyLikedMatchNotInLibrary = null;
    }

    /// <summary>Last full liked-songs library match sync (background).</summary>
    public DateTime? SpotifyLikedMatchStatsUpdatedAtUtc { get; set; }

    public int? SpotifyLikedMatchTotal { get; set; }
    public int? SpotifyLikedMatchInLibrary { get; set; }
    public int? SpotifyLikedMatchPossible { get; set; }
    public int? SpotifyLikedMatchNotInLibrary { get; set; }
}
