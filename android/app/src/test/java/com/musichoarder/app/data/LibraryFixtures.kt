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
