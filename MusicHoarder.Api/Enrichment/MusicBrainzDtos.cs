using System.Text.Json.Serialization;

namespace MusicHoarder.Api.Enrichment;

// Wire-format DTOs for the MusicBrainz web service (musicbrainz.org/ws/2, fmt=json).
// Deserialized by MusicBrainzWebService and mapped to the public domain records by
// MusicBrainzResponseMapper.

internal sealed class MusicBrainzIsrcDto
{
    [JsonPropertyName("recordings")]
    public List<MusicBrainzRecordingDto>? Recordings { get; set; }
}

internal sealed class MusicBrainzRecordingSearchDto
{
    [JsonPropertyName("recordings")]
    public List<MusicBrainzRecordingDto>? Recordings { get; set; }
}

internal sealed class MusicBrainzRecordingDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("length")]
    public int? Length { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }

    [JsonPropertyName("artist-credit")]
    public List<MusicBrainzArtistCreditDto>? ArtistCredit { get; set; }

    [JsonPropertyName("releases")]
    public List<MusicBrainzReleaseDto>? Releases { get; set; }

    [JsonPropertyName("isrcs")]
    public List<string>? Isrcs { get; set; }

    [JsonPropertyName("genres")]
    public List<MusicBrainzGenreDto>? Genres { get; set; }

    [JsonPropertyName("tags")]
    public List<MusicBrainzGenreDto>? Tags { get; set; }
}

internal sealed class MusicBrainzArtistCreditDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("joinphrase")]
    public string? JoinPhrase { get; set; }

    [JsonPropertyName("artist")]
    public MusicBrainzArtistDto? Artist { get; set; }
}

internal sealed class MusicBrainzArtistDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("sort-name")]
    public string? SortName { get; set; }
}

// Shared by MusicBrainz `genres` and `tags` — both are {name, count} lists.
internal sealed class MusicBrainzGenreDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }
}

internal sealed class MusicBrainzLabelInfoDto
{
    [JsonPropertyName("catalog-number")]
    public string? CatalogNumber { get; set; }

    [JsonPropertyName("label")]
    public MusicBrainzLabelDto? Label { get; set; }
}

internal sealed class MusicBrainzLabelDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class MusicBrainzReleaseDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("label-info")]
    public List<MusicBrainzLabelInfoDto>? LabelInfo { get; set; }

    [JsonPropertyName("release-group")]
    public MusicBrainzReleaseGroupDto? ReleaseGroup { get; set; }

    [JsonPropertyName("media")]
    public List<MusicBrainzMediaDto>? Media { get; set; }
}

internal sealed class MusicBrainzReleaseGroupDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("primary-type")]
    public string? PrimaryType { get; set; }

    [JsonPropertyName("secondary-types")]
    public List<string?>? SecondaryTypes { get; set; }

    [JsonPropertyName("first-release-date")]
    public string? FirstReleaseDate { get; set; }
}

internal sealed class MusicBrainzMediaDto
{
    [JsonPropertyName("position")]
    public int? Position { get; set; }

    [JsonPropertyName("track-count")]
    public int? TrackCount { get; set; }

    [JsonPropertyName("tracks")]
    public List<MusicBrainzTrackDto>? Tracks { get; set; }
}

// --- Release-detail (full tracklist) DTOs ---

internal sealed class MusicBrainzReleaseDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("artist-credit")]
    public List<MusicBrainzArtistCreditDto>? ArtistCredit { get; set; }

    [JsonPropertyName("media")]
    public List<MusicBrainzMediaDto>? Media { get; set; }
}

internal sealed class MusicBrainzReleaseSearchDto
{
    [JsonPropertyName("releases")]
    public List<MusicBrainzReleaseSearchItemDto>? Releases { get; set; }
}

internal sealed class MusicBrainzReleaseSearchItemDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }

    [JsonPropertyName("track-count")]
    public int? TrackCount { get; set; }

    [JsonPropertyName("score")]
    public int? Score { get; set; }
}

internal sealed class MusicBrainzTrackDto
{
    [JsonPropertyName("position")]
    public int? Position { get; set; }

    [JsonPropertyName("number")]
    public string? Number { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("length")]
    public int? Length { get; set; }

    [JsonPropertyName("recording")]
    public MusicBrainzRecordingRefDto? Recording { get; set; }
}

internal sealed class MusicBrainzRecordingRefDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("length")]
    public int? Length { get; set; }
}
