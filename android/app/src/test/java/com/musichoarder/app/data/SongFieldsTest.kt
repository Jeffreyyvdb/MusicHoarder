package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * Pins the date handling to what `Date.parse` does in the browser, because "recently added" and
 * "recently liked" have to mean the same thing on both clients.
 *
 * The expected epoch values are written out rather than computed, so a rewrite of the parser cannot
 * quietly agree with itself.
 */
class SongFieldsTest {

    @Test
    fun `parses the Z form`() {
        assertEquals(1_709_294_400_000L, parseIsoUtcMillis("2024-03-01T12:00:00Z"))
    }

    @Test
    fun `a stamp with no zone is read as UTC`() {
        // Every column these come from is named ...AtUtc. JavaScript would read this as local time,
        // which is a bug on that side rather than behaviour worth reproducing.
        assertEquals(
            parseIsoUtcMillis("2024-03-01T12:00:00Z"),
            parseIsoUtcMillis("2024-03-01T12:00:00"),
        )
    }

    @Test
    fun `keeps only the first three of dotnet's seven fractional digits`() {
        // SimpleDateFormat's SSS reads .1234567 as 1,234,567 milliseconds and lands two weeks late.
        assertEquals(1_709_294_400_123L, parseIsoUtcMillis("2024-03-01T12:00:00.1234567Z"))
        assertEquals(1_709_294_400_120L, parseIsoUtcMillis("2024-03-01T12:00:00.12Z"))
    }

    @Test
    fun `applies a numeric offset`() {
        assertEquals(
            parseIsoUtcMillis("2024-03-01T10:00:00Z"),
            parseIsoUtcMillis("2024-03-01T12:00:00+02:00"),
        )
        assertEquals(
            parseIsoUtcMillis("2024-03-01T14:30:00Z"),
            parseIsoUtcMillis("2024-03-01T12:00:00-0230"),
        )
    }

    @Test
    fun `a date on its own is midnight UTC`() {
        assertEquals(1_709_251_200_000L, parseIsoUtcMillis("2024-03-01"))
    }

    @Test
    fun `anything unparseable is zero, which sorts last`() {
        assertEquals(0L, parseIsoUtcMillis(null))
        assertEquals(0L, parseIsoUtcMillis(""))
        assertEquals(0L, parseIsoUtcMillis("not a date"))
        assertEquals(0L, parseIsoUtcMillis("2024-13-01T00:00:00Z"))
        assertEquals(0L, parseIsoUtcMillis("2024-03-01T12:00:00Zjunk"))
    }

    // ---- added / liked ordering ------------------------------------------------------------

    private val jan2020 = parseIsoUtcMillis("2020-01-01T00:00:00Z")
    private val jun2023 = parseIsoUtcMillis("2023-06-01T00:00:00Z")
    private val mar2024 = parseIsoUtcMillis("2024-03-01T00:00:00Z")

    @Test
    fun `an older Spotify save beats the download date`() {
        // The whole point: a years-old like drips in with today's acquisition stamp and would
        // otherwise sit at the top of "recently added" next to things you actually just got.
        assertEquals(
            jan2020,
            songAddedMillis(spotifyAddedAt = jan2020, acquiredAt = mar2024, libraryBuiltAt = 0, indexedAt = 0),
        )
    }

    @Test
    fun `the date is only ever pulled backwards`() {
        // A track ripped years before it was saved on Spotify keeps its own acquisition date.
        assertEquals(
            jan2020,
            songAddedMillis(spotifyAddedAt = mar2024, acquiredAt = jan2020, libraryBuiltAt = 0, indexedAt = 0),
        )
    }

    @Test
    fun `rows with no acquisition stamp fall back to the oldest churn-prone one`() {
        assertEquals(
            jun2023,
            songAddedMillis(spotifyAddedAt = 0, acquiredAt = 0, libraryBuiltAt = mar2024, indexedAt = jun2023),
        )
        assertEquals(0L, songAddedMillis(0, 0, 0, 0))
    }

    @Test
    fun `an unliked track reports no like time even with a Spotify save date`() {
        assertEquals(0L, songLikedMillis(likedAt = 0, spotifyLikedAt = jan2020, spotifyAddedAt = jan2020))
    }

    @Test
    fun `the like time is the earliest evidence of the like`() {
        // The auto-like sweep stamps thousands of rows with one `now`; the Spotify date is the real
        // moment, so it wins.
        assertEquals(
            jan2020,
            songLikedMillis(likedAt = mar2024, spotifyLikedAt = jan2020, spotifyAddedAt = jun2023),
        )
        // A playlist add is not a like, so it only counts when there is no Liked Songs date.
        assertEquals(
            jun2023,
            songLikedMillis(likedAt = mar2024, spotifyLikedAt = 0, spotifyAddedAt = jun2023),
        )
    }

    @Test
    fun `the Spotify save date prefers the Liked Songs stamp`() {
        assertEquals(jan2020, spotifyAddedMillis(spotifyLikedAt = jan2020, spotifyAddedAt = jun2023))
        assertEquals(jun2023, spotifyAddedMillis(spotifyLikedAt = 0, spotifyAddedAt = jun2023))
    }

    // ---- misc ------------------------------------------------------------------------------

    @Test
    fun `enrichment status is read from either the number or the name`() {
        assertEquals(EnrichmentState.Complete, mapEnrichmentState("1"))
        assertEquals(EnrichmentState.NeedsReview, mapEnrichmentState("2"))
        assertEquals(EnrichmentState.Failed, mapEnrichmentState("3"))
        assertEquals(EnrichmentState.NeedsReview, mapEnrichmentState("NeedsReview"))
        assertEquals(EnrichmentState.Complete, mapEnrichmentState("Matched"))
        assertEquals(EnrichmentState.Pending, mapEnrichmentState("99"))
        assertEquals(EnrichmentState.Pending, mapEnrichmentState(null))
    }

    @Test
    fun `discrete credits split on semicolons`() {
        assertEquals(
            listOf("21 Savage", "Travis Scott", "Metro Boomin"),
            discreteArtists("21 Savage; Travis Scott; Metro Boomin", "Metro Boomin"),
        )
        assertEquals(listOf("Kid Cudi"), discreteArtists(null, "Kid Cudi"))
        assertEquals(listOf("Kid Cudi"), discreteArtists("  ;  ", "Kid Cudi"))
    }
}
