package com.musichoarder.app.data

import java.text.Normalizer

/**
 * What a search query means — a port of `frontend/src/lib/search/match.ts`, tested case for case.
 *
 * Both clients used to search by asking `contains(query, ignoreCase = true)` of one field at a
 * time. That is one contiguous substring of one field, which is not how anybody types a song:
 * "best friend with fall out" never matched "Best Friend (with Fall Out Boy)" because the bracket
 * sits between the words, "juice wrld best friend" never matched because the query spans the title
 * and the artist, and "beyonce" never matched "Beyoncé".
 *
 * So a query is split into terms, and a row matches when **every** term is found in **any** of its
 * fields, over text folded to letters and digits. Punctuation, brackets, diacritics and term order
 * stop mattering; the terms themselves still have to all be there, so a query keeps narrowing the
 * list as you type.
 *
 * The web carries a second half this does not need — a relevance score for its command palette,
 * which shows the best eight rather than the whole filtered list. The phone has no global palette;
 * its search filters a list in place, and every match is listed.
 */

private val APOSTROPHES = Regex("['‘’ʼ`´]")
private val NON_ALPHANUMERIC = Regex("[^\\p{L}\\p{N}]+")
private val COMBINING_MARKS = Regex("\\p{Mn}+")

/**
 * Fold text to the form terms are compared in: lowercase letters and digits, single-spaced.
 *
 * Apostrophes are *removed* rather than spaced, so "Girl's" folds to `girls` and a query without
 * the apostrophe still matches; every other non-alphanumeric run becomes a space.
 */
fun normalizeForSearch(value: String): String =
    Normalizer.normalize(value, Normalizer.Form.NFD)
        .replace(COMBINING_MARKS, "")
        .lowercase()
        .replace(APOSTROPHES, "")
        .replace(NON_ALPHANUMERIC, " ")
        .trim()

/** The terms a candidate has to carry, in any field and any order. Empty means "no query". */
fun searchTerms(query: String): List<String> {
    val normalized = normalizeForSearch(query)
    return if (normalized.isEmpty()) emptyList() else normalized.split(' ')
}

/**
 * Every term present in at least one of `fields`.
 *
 * A term matches a field at a word boundary *or* mid-word: someone typing "wrld" should find
 * "Juice WRLD", and someone typing a fragment they half-remember should still narrow the list.
 */
fun matchesTerms(fields: List<String>, terms: List<String>): Boolean =
    terms.all { term -> fields.any { it.contains(term) } }

/** Fold `fields` and match `query` against them in one call — for one-off checks, not hot loops. */
fun matchesQuery(query: String, vararg fields: String?): Boolean {
    val terms = searchTerms(query)
    if (terms.isEmpty()) return true
    return matchesTerms(normalizeFields(*fields), terms)
}

/** Fold a row's fields for matching, dropping the blank ones. */
fun normalizeFields(vararg fields: String?): List<String> =
    fields.mapNotNull { field ->
        field?.let(::normalizeForSearch)?.takeIf { it.isNotEmpty() }
    }

/**
 * Filter `rows` by `query`, folding each row's fields once.
 *
 * Kept as one call because every caller here filters a whole list: the folding cost is per row per
 * keystroke either way (the lists are re-derived from the repository state), and a shared index
 * would have to be threaded through the fold's pure functions.
 */
fun <T> filterBySearch(rows: List<T>, query: String, fieldsOf: (T) -> List<String?>): List<T> {
    val terms = searchTerms(query)
    if (terms.isEmpty()) return rows
    return rows.filter { row -> matchesTerms(normalizeFields(*fieldsOf(row).toTypedArray()), terms) }
}
