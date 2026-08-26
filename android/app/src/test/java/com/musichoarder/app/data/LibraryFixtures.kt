package com.musichoarder.app.data

import kotlinx.serialization.json.JsonPrimitive

/**
 * Builds `/songs` rows for the grouping and filtering tests.
 *
 * Deliberately goes through [ApiSong.toTrack] rather than constructing a [Track] directly, so the
 * mapping the app actually runs is what the assertions see.
 */
internal fun song(
    id: Int,
    title: String = "Track $id",
    artist: String? = "An Artist",
    artists: String? = null,
    albumArtist: String? = null,
    album: String? = "An Album",
    year: Int? = null,
    trackNumber: Int? = null,
    destinationPath: String? = "/library/An Artist/An Album/$id.flac",
    durationSeconds: Int? = 200,
    hasCoverArt: Boolean = false,
    acquiredAtUtc: String? = null,
    likedAtUtc: String? = null,
    spotifyLikedAtUtc: String? = null,
    spotifyAddedAtUtc: String? = null,
    playCount: Int? = null,
    lastPlayedAtUtc: String? = null,
    releaseClassification: String? = null,
    originKind: String? = "Downloaded",
    originSource: String? = null,
    acquisitionIntent: String? = "Explicit",
    hasSyncedLyrics: Boolean = false,
    hasMusicVideo: Boolean = false,
    enrichmentStatus: String? = null,
): Track = ApiSong(
    id = id,
    fileName = "$id.flac",
    title = title,
    artist = artist,
    artists = artists,
    albumArtist = albumArtist,
    album = album,
    year = year,
    trackNumber = trackNumber,
    durationSeconds = durationSeconds,
    hasCoverArt = hasCoverArt,
    acquiredAtUtc = acquiredAtUtc,
    destinationPath = destinationPath,
    libraryBuildStatus = JsonPrimitive(3),
    enrichmentStatus = enrichmentStatus?.let { JsonPrimitive(it) },
    likedAtUtc = likedAtUtc,
    spotifyAddedAtUtc = spotifyAddedAtUtc,
    spotifyLikedAtUtc = spotifyLikedAtUtc,
    playCount = playCount,
    lastPlayedAtUtc = lastPlayedAtUtc,
    releaseClassification = releaseClassification,
    originKind = originKind,
    originSource = originSource,
    acquisitionIntent = acquisitionIntent,
    hasSyncedLyrics = hasSyncedLyrics,
    hasMusicVideo = hasMusicVideo,
).toTrack()

/**
 * One album card as `GET /api/albums` would send it for these tracks, joined back to them.
 *
 * The grouping rules are the server's — pinned by `AlbumProjectionTests` in the API suite — so a test
 * that needs an album *states* one rather than deriving it here. Deriving it would put a second
 * implementation of those rules back in this repository, which is the thing that kept going wrong.
 */
internal fun albumOf(
    tracks: List<Track>,
    name: String = tracks.first().album,
    artist: String = tracks.first().albumArtist,
    key: String = "${artist.lowercase()}::${name.lowercase()}",
    folderKeys: List<String> = listOf(key),
    year: Int? = tracks.firstNotNullOfOrNull { it.year },
    addedAtUtc: String? = null,
): Album = hydrateAlbums(
    listOf(
        AlbumSummaryDto(
            key = key,
            folderKeys = folderKeys,
            nameKey = "${artist.lowercase()}::${name.lowercase()}",
            title = name,
            artist = artist,
            year = year,
            trackCount = tracks.size,
            durationSeconds = tracks.sumOf { it.durationSeconds },
            playCount = tracks.sumOf { it.playCount },
            addedAtUtc = addedAtUtc,
            trackIds = tracks.map { it.id },
        ),
    ),
    tracks.associateBy { it.id },
).single()
