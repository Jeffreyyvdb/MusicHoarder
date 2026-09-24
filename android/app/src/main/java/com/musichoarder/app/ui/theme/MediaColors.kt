package com.musichoarder.app.ui.theme

import androidx.compose.runtime.Immutable
import androidx.compose.ui.graphics.Color

/**
 * Now Playing's media appearance — the web's `.dark.mh-np` block in `SongDetailHost.svelte`.
 *
 * The player is dark whatever the app theme, like Apple Music and like the web overlay: white text
 * over a wash of the cover, dimmed per cover (`CoverDim.kt`) so that even the wash's brightest cell
 * sits at or below about 70/255 grey. That dim is what makes the translucent white below safe
 * everywhere on the screen — white clears 9.4:1, the 72% secondary tone 5.8:1 and the 55% inactive
 * lyric lines 4.2:1 (they are 28sp bold, which needs 3:1). These are the one sanctioned use of alpha
 * text in either client.
 *
 * The fills and hairlines are white washes too, so they pick up the cover's colour instead of
 * reading as grey smudges on it. The tint is the one thing no dim can promise — the brand green
 * lands near 2.5:1 on a magenta cover — so, as in Apple Music, what you tap in here is white and
 * the liked heart carries its own light green ([MhMediaTokens.liked]). The warning and destructive
 * text tones lift to pastels that hold 4.5:1 on a cell over the wash, and the focus ring is solid
 * white — the web's values, which it chose over a translucent ring that all but vanished.
 *
 * Menus opened from the player do NOT take this palette: they are solid popovers, and keep the
 * plain dark tokens (a green check on #2C2C2E), exactly as the web's portaled menus do.
 */
fun MhColors.mediaAppearance(): MhColors {
    val secondaryText = if (isHighContrast) white(0.86f) else white(0.72f)
    return copy(
        background = Color.Black,
        foreground = Color.White,
        primary = Color.White,
        primaryForeground = Color.Black,
        ring = Color.White,
        mutedForeground = secondaryText,
        // Tertiary text would sit too low on a coloured wash; both text tiers use the 72% tone.
        mutedForegroundDim = secondaryText,
        warningText = Color(0xFFFFC266),
        destructiveText = Color(0xFFFFC2BE),
        secondary = white(0.14f),
        secondaryHover = white(0.2f),
        muted = white(0.1f),
        accent = white(0.1f),
        input = white(0.12f),
        card = white(0.08f),
        separator = if (isHighContrast) white(0.4f) else white(0.14f),
        border = if (isHighContrast) white(0.45f) else white(0.16f),
        segmentedThumb = white(0.28f),
    )
}

/** The media appearance's own tokens, which have no twin in the app palette. */
@Immutable
data class MhMediaTokens(
    /** `--np-fill`: the capsule behind an active bottom-row toggle. */
    val fill: Color,
    /** `--np-liked`: the liked heart. A light green of its own, since the tint is white in here. */
    val liked: Color,
    /** The lyric lines that are not being sung: `text-foreground/55`. */
    val lyricInactive: Color,
    /** The drag handle at the top: `bg-foreground/35`. */
    val grabber: Color,
)

fun mediaTokens(highContrast: Boolean) = MhMediaTokens(
    fill = if (highContrast) white(0.2f) else white(0.12f),
    liked = Color(0xFF4CE070),
    lyricInactive = white(0.55f),
    grabber = white(0.35f),
)

private fun white(alpha: Float) = Color.White.copy(alpha = alpha)
