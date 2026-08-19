package com.musichoarder.app.ui.theme

import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color

/**
 * The web app's design tokens, converted from the OKLCH values in `frontend/src/app.css` to sRGB.
 *
 * The names are deliberately the CSS ones (`mutedForeground`, `card`, `border`…) so a change on
 * either side is easy to mirror: find the token in `app.css`, convert, replace here. Material's own
 * [androidx.compose.material3.ColorScheme] slots are mapped from these in `Theme.kt` so the stock
 * components (sliders, text fields, ripples) land on the same palette.
 */
@Immutable
data class MhColors(
    val background: Color,
    val foreground: Color,
    val card: Color,
    val popover: Color,
    val primary: Color,
    val primaryForeground: Color,
    val secondary: Color,
    val secondaryForeground: Color,
    val muted: Color,
    val mutedForeground: Color,
    val accent: Color,
    val accentForeground: Color,
    val destructive: Color,
    val destructiveForeground: Color,
    val border: Color,
    val input: Color,
    val ring: Color,
    val surfaceSunken: Color,
    val isDark: Boolean,
)

val MhLightColors = MhColors(
    background = Color(0xFFF8FAFD),
    foreground = Color(0xFF11161F),
    card = Color(0xFFFFFFFF),
    popover = Color(0xFFFFFFFF),
    primary = Color(0xFF007A11),
    primaryForeground = Color(0xFFFBFCFD),
    secondary = Color(0xFFEEF2F9),
    secondaryForeground = Color(0xFF232933),
    muted = Color(0xFFEFF2F7),
    mutedForeground = Color(0xFF4F5661),
    accent = Color(0xFFD8EFD8),
    accentForeground = Color(0xFF133015),
    destructive = Color(0xFFCC272E),
    destructiveForeground = Color(0xFFFCFCFC),
    border = Color(0xFFDADEE5),
    input = Color(0xFFE1E5EB),
    ring = Color(0xFF007A11),
    surfaceSunken = Color(0xFFF3F5F9),
    isDark = false,
)

val MhDarkColors = MhColors(
    background = Color(0xFF060709),
    foreground = Color(0xFFEEEEEE),
    card = Color(0xFF0C0D0F),
    popover = Color(0xFF101214),
    primary = Color(0xFF11AD32),
    primaryForeground = Color(0xFF060709),
    secondary = Color(0xFF191B1D),
    secondaryForeground = Color(0xFFCECECE),
    muted = Color(0xFF151618),
    mutedForeground = Color(0xFF717171),
    accent = Color(0xFF1E1F22),
    accentForeground = Color(0xFFEEEEEE),
    destructive = Color(0xFFCC272E),
    destructiveForeground = Color(0xFFEEEEEE),
    border = Color(0xFF202224),
    input = Color(0xFF151618),
    ring = Color(0xFF11AD32),
    surfaceSunken = Color(0xFF030304),
    isDark = true,
)

val LocalMhColors = staticCompositionLocalOf { MhDarkColors }
