package com.musichoarder.app.ui.theme

import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

/**
 * The web app's design tokens, mirrored from `frontend/src/app.css` — Apple's system palette under
 * shadcn's names, which the web now writes as sRGB directly.
 *
 * The names are deliberately the CSS ones (`mutedForeground`, `card`, `separator`…) so a change on
 * either side is easy to mirror: find the token in `app.css`, copy the value, replace it here.
 * Material's own [androidx.compose.material3.ColorScheme] slots are mapped from these in `Theme.kt`
 * so the stock components (menus, dialogs, text fields, ripples) land on the same palette.
 *
 * Surfaces follow iOS's grouped hierarchy instead of drawing structure with borders: [background]
 * for plain pages, [backgroundGrouped] under grouped cells, [card] for those cells, [cardElevated]
 * for cells inside a sheet or dialog, [popover] for menus.
 *
 * [secondary], [muted], [accent], [input], [separator] and [switchOff] are TRANSLUCENT, like iOS's
 * fill colours, so they read on any of those surfaces. Never `.copy(alpha = …)` one of them: in
 * Compose that *replaces* the token's alpha rather than multiplying it, so `input.copy(alpha = 0.6f)`
 * turns a 12% grey wash into a 60% grey slab.
 */
@Immutable
data class MhColors(
    val background: Color,
    /** `--background-grouped`: the page under grouped cells (hubs, settings, info). */
    val backgroundGrouped: Color,
    val foreground: Color,
    val card: Color,
    /** `--card-elevated`: cells inside a sheet or dialog — iOS moves them up a step. */
    val cardElevated: Color,
    /** `--sheet`: a bottom sheet or dialog whose content is grouped. */
    val sheet: Color,
    val popover: Color,
    val primary: Color,
    val primaryForeground: Color,
    /** Fill-secondary: gray buttons and chips. Translucent. */
    val secondary: Color,
    /** `--secondary-hover`: the pressed/hover step of [secondary]. Translucent. */
    val secondaryHover: Color,
    val secondaryForeground: Color,
    /** Fill-tertiary: fields, segmented tracks, skeletons. Translucent. */
    val muted: Color,
    val mutedForeground: Color,
    /** `--muted-foreground-dim`: tertiary text, one step below [mutedForeground]. Use instead of
     *  an alpha-dimmed [mutedForeground], which drops text below 4.5:1. Never on a fill. */
    val mutedForegroundDim: Color,
    /** Fill-quaternary: row press and selection. Translucent, neutral in both modes. */
    val accent: Color,
    val accentForeground: Color,
    val destructive: Color,
    val destructiveForeground: Color,
    /** `--destructive-text`: [destructive] as *text* (and on its own tints). [destructive] itself
     *  is the fill under [destructiveForeground]. */
    val destructiveText: Color,
    /** `--warning`: a fill or a dot. */
    val warning: Color,
    /** `--warning-text`: [warning] as text, a step deeper so it clears 4.5:1 on white. */
    val warningText: Color,
    /** Opaque, used sparingly: a control that keeps a stroke. List hairlines use [separator]. */
    val border: Color,
    /** `--separator`: list hairlines, translucent like UIKit's. */
    val separator: Color,
    /** A field's FILL (not a stroke). Translucent. */
    val input: Color,
    /** `--switch-off`: the off track. State is carried by the thumb and the tint, as on iOS. */
    val switchOff: Color,
    /** `--segmented-thumb`: the selected segment of a segmented control. */
    val segmentedThumb: Color,
    val ring: Color,
    /** Wells INSIDE a cell only; on [backgroundGrouped] it is invisible, so use [muted] there. */
    val surfaceSunken: Color,
    /** `--chrome`: the translucent fill of the controls that float (tab bar, mini player). */
    val chrome: Color,
    /** `--chrome-rim`: the half-pixel edge that keeps [chrome] off whatever it floats over. */
    val chromeRim: Color,
    /** `--chrome-solid`: [chrome] where there is nothing to blur, or transparency is reduced. */
    val chromeSolid: Color,
    val isDark: Boolean,
    /** One of the increased-contrast palettes; the player's media appearance lifts with it. */
    val isHighContrast: Boolean = false,
)

/** `rgb(r g b / a)` exactly as `app.css` writes it, so the translucent fills can be diffed by eye. */
private fun rgba(red: Int, green: Int, blue: Int, alpha: Float): Color =
    Color(red, green, blue, (alpha * 255f + 0.5f).toInt())

val MhLightColors = MhColors(
    background = Color(0xFFFFFFFF),
    backgroundGrouped = Color(0xFFF2F2F7),
    foreground = Color(0xFF000000),
    card = Color(0xFFFFFFFF),
    cardElevated = Color(0xFFFFFFFF),
    sheet = Color(0xFFF2F2F7),
    popover = Color(0xFFFFFFFF),
    // 6.72:1 on white, 6.03 on grouped; white on the fill 6.72.
    primary = Color(0xFF006B1F),
    primaryForeground = Color(0xFFFFFFFF),
    secondary = rgba(120, 120, 128, 0.16f),
    secondaryHover = rgba(120, 120, 128, 0.24f),
    secondaryForeground = Color(0xFF000000),
    muted = rgba(118, 118, 128, 0.12f),
    // 6.35:1 on white, 5.69 on grouped.
    mutedForeground = Color(0xFF5F5F64),
    // 5.23:1 on white, 4.69 on grouped.
    mutedForegroundDim = Color(0xFF6C6C70),
    accent = rgba(116, 116, 128, 0.08f),
    accentForeground = Color(0xFF000000),
    destructive = Color(0xFFCC272E),
    destructiveForeground = Color(0xFFFFFFFF),
    destructiveText = Color(0xFFB71824),
    warning = Color(0xFFFF9500),
    // 5.28:1 on white, 4.73 on grouped.
    warningText = Color(0xFFC93400),
    border = Color(0xFFE5E5EA),
    separator = rgba(60, 60, 67, 0.2f),
    input = rgba(118, 118, 128, 0.12f),
    switchOff = rgba(120, 120, 128, 0.16f),
    segmentedThumb = Color(0xFFFFFFFF),
    ring = Color(0xFF006B1F),
    surfaceSunken = Color(0xFFF2F2F7),
    chrome = rgba(255, 255, 255, 0.85f),
    chromeRim = rgba(0, 0, 0, 0.08f),
    chromeSolid = Color(0xFFF9F9F9),
    isDark = false,
)

val MhDarkColors = MhColors(
    background = Color(0xFF000000),
    backgroundGrouped = Color(0xFF000000),
    foreground = Color(0xFFFFFFFF),
    card = Color(0xFF1C1C1E),
    cardElevated = Color(0xFF2C2C2E),
    sheet = Color(0xFF1C1C1E),
    popover = Color(0xFF2C2C2E),
    // 8.53:1 on black, 6.91 on #1C1C1E; black on the fill 8.53.
    primary = Color(0xFF2FBD55),
    primaryForeground = Color(0xFF000000),
    secondary = rgba(120, 120, 128, 0.32f),
    secondaryHover = rgba(120, 120, 128, 0.40f),
    secondaryForeground = Color(0xFFFFFFFF),
    muted = rgba(118, 118, 128, 0.24f),
    // 8.16:1 on black, 6.61 on #1C1C1E.
    mutedForeground = Color(0xFFA1A1A6),
    // 6.44:1 on black, 5.22 on #1C1C1E (4.27 on #2C2C2E — the web lifts it there to #98989D).
    // Not mirrored: no menu or sheet here draws dim text. Add the lift before one does.
    mutedForegroundDim = Color(0xFF8E8E93),
    accent = rgba(118, 118, 128, 0.18f),
    accentForeground = Color(0xFFFFFFFF),
    destructive = Color(0xFFCC272E),
    destructiveForeground = Color(0xFFFFFFFF),
    // 7.45:1 on black, 6.03 on #1C1C1E, 4.94 on #2C2C2E.
    destructiveText = Color(0xFFFF6961),
    warning = Color(0xFFFF9F0A),
    warningText = Color(0xFFFF9F0A),
    border = Color(0xFF2C2C2E),
    separator = rgba(84, 84, 88, 0.45f),
    input = rgba(118, 118, 128, 0.24f),
    switchOff = rgba(120, 120, 128, 0.32f),
    segmentedThumb = Color(0xFF636366),
    ring = Color(0xFF2FBD55),
    surfaceSunken = Color(0xFF1C1C1E),
    chrome = rgba(28, 28, 30, 0.88f),
    chromeRim = rgba(255, 255, 255, 0.14f),
    chromeSolid = Color(0xFF1C1C1E),
    isDark = true,
)

/**
 * `@media (prefers-contrast: more)`: Android's contrast setting (API 34+). Every opaque text token
 * moves to ≥ 6:1, strokes clear the 3:1 non-text floor, the translucent fills gain ×1.8 alpha, and
 * the floating chrome goes solid — the web's `.mh-glass` fallback.
 */
val MhLightContrastColors = MhLightColors.copy(
    // 10.94:1 on white.
    mutedForeground = Color(0xFF3C3C43),
    // 9.09:1 on white.
    mutedForegroundDim = Color(0xFF48484D),
    border = Color(0xFF8A8A8E),
    separator = Color(0xFF8A8A8E),
    // 8.25:1 on white.
    primary = Color(0xFF005C1A),
    ring = Color(0xFF005C1A),
    destructiveText = Color(0xFF9F0F1A),
    warningText = Color(0xFFA52A00),
    secondary = rgba(120, 120, 128, 0.29f),
    secondaryHover = rgba(120, 120, 128, 0.43f),
    muted = rgba(118, 118, 128, 0.22f),
    input = rgba(118, 118, 128, 0.22f),
    accent = rgba(116, 116, 128, 0.14f),
    switchOff = rgba(120, 120, 128, 0.5f),
    chrome = MhLightColors.chromeSolid,
    isHighContrast = true,
)

val MhDarkContrastColors = MhDarkColors.copy(
    // 12.47:1 on black.
    mutedForeground = Color(0xFFC7C7CC),
    // 9.50:1 on black.
    mutedForegroundDim = Color(0xFFAEAEB2),
    border = Color(0xFF7C7C80),
    separator = Color(0xFF7C7C80),
    // 12.20:1 on black.
    primary = Color(0xFF4CE070),
    ring = Color(0xFF4CE070),
    destructiveText = Color(0xFFFF8A82),
    warningText = Color(0xFFFFB340),
    secondary = rgba(120, 120, 128, 0.58f),
    secondaryHover = rgba(120, 120, 128, 0.72f),
    muted = rgba(118, 118, 128, 0.43f),
    input = rgba(118, 118, 128, 0.43f),
    accent = rgba(118, 118, 128, 0.32f),
    switchOff = rgba(120, 120, 128, 0.58f),
    chrome = MhDarkColors.chromeSolid,
    isHighContrast = true,
)

val LocalMhColors = staticCompositionLocalOf { MhDarkColors }
