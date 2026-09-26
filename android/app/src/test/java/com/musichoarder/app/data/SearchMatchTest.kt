package com.musichoarder.app.data

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Pins the query semantics to `frontend/src/lib/search/match.test.ts` — the same cases, so the two
 * clients can never disagree about what a search finds.
 */
class SearchMatchTest {

    @Test
    fun `folds punctuation, case and diacritics`() {
        assertEquals("best friend with fall out boy", normalizeForSearch("Best Friend (with Fall Out Boy)"))
        assertEquals("beyonce", normalizeForSearch("Beyoncé"))
        assertEquals("hip hop rap", normalizeForSearch("  Hip-Hop   /  Rap "))
    }

    @Test
    fun `drops apostrophes instead of splitting on them`() {
        assertEquals("girls best friend", normalizeForSearch("Girl's Best Friend"))
        assertEquals("get rich or die tryin", normalizeForSearch("Get Rich Or Die Tryin’"))
    }

    @Test
    fun `keeps digits and non-Latin letters`() {
        assertEquals("the party never ends 2 0", normalizeForSearch("The Party Never Ends 2.0"))
        assertEquals("пикник", normalizeForSearch("Пикник"))
    }

    @Test
    fun `splits into terms and treats a blank query as no query`() {
        assertEquals(listOf("best", "friend"), searchTerms("  best   friend "))
        assertEquals(emptyList<String>(), searchTerms("   "))
        assertEquals(emptyList<String>(), searchTerms("!!!"))
    }

    private val song = normalizeFields(
        "Best Friend (with Fall Out Boy)",
        "Juice WRLD, Fall Out Boy",
        "The Party Never Ends 2.0",
    )

    @Test
    fun `matches across the punctuation the old substring search tripped on`() {
        assertTrue(matchesTerms(song, searchTerms("best friend with fall out")))
    }

    @Test
    fun `matches terms spread over different fields, in any order`() {
        assertTrue(matchesTerms(song, searchTerms("juice best friend")))
        assertTrue(matchesTerms(song, searchTerms("party best friend")))
    }

    @Test
    fun `still requires every term`() {
        assertFalse(matchesTerms(song, searchTerms("best friend drake")))
    }

    @Test
    fun `matches mid-word fragments`() {
        assertTrue(matchesTerms(song, searchTerms("wrld")))
        assertTrue(matchesTerms(song, searchTerms("riend")))
    }

    @Test
    fun `an empty query matches everything`() {
        assertTrue(matchesTerms(song, emptyList()))
        assertTrue(matchesQuery("", "anything"))
    }

    @Test
    fun `ignores null and empty fields`() {
        assertFalse(matchesQuery("unknown", "Title", null, ""))
        assertTrue(matchesQuery("title", "Title", null))
    }

    private data class Row(val title: String, val artist: String, val album: String)

    private val rows = listOf(
        Row("Girl's Best Friend (feat. Ty Dolla \$ign)", "2 Chainz", "Rap or Go"),
        Row("Best Friend", "50 Cent", "Get Rich Or Die Tryin’"),
        Row("Best Friend (Remix)", "50 Cent and Olivia", "Best Of 50 Cent"),
        Row("Best Friend (feat. Tory Lanez)", "A Boogie", "International Artist"),
        Row("Need a Best Friend", "A Boogie", "Hoodie SZN"),
        Row("Already Best Friends", "Jack Harlow", "Thats What They All Say"),
        Row("Best Friend (with Fall Out Boy)", "Juice WRLD, Fall Out Boy", "TPNE 2.0"),
    )

    private fun search(query: String) =
        filterBySearch(rows, query) { listOf(it.title, it.artist, it.album) }

    @Test
    fun `finds the track the old search could not express`() {
        assertEquals(
            listOf("Best Friend (with Fall Out Boy)"),
            search("best friend with fall out").map { it.title },
        )
    }

    @Test
    fun `finds a track by artist and title together`() {
        assertEquals(listOf("Juice WRLD, Fall Out Boy"), search("juice best friend").map { it.artist })
    }

    @Test
    fun `keeps every match, in list order, and passes an empty query through`() {
        assertEquals(
            listOf(
                "Girl's Best Friend (feat. Ty Dolla \$ign)",
                "Best Friend",
                "Best Friend (Remix)",
                "Best Friend (feat. Tory Lanez)",
                "Need a Best Friend",
                // Plural: "friend" is found inside "Friends", so the row is a match like any other.
                "Already Best Friends",
                "Best Friend (with Fall Out Boy)",
            ),
            search("best friend").map { it.title },
        )
        assertEquals(rows.size, search("").size)
    }
}
