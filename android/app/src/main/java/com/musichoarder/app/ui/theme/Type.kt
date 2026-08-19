package com.musichoarder.app.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.LineHeightStyle
import androidx.compose.ui.unit.sp

/**
 * The web's type scale, in sp.
 *
 * `app.css` sets `--font-sans` to the platform UI stack, which on Android already resolves to Roboto
 * — so [FontFamily.Default] *is* the web font here, no bundled face needed. The named chrome sizes
 * (`--text-nav: 13px` and friends) plus the body sizes the pages use as arbitrary utilities map onto
 * Material's slots below; tracking is tightened slightly the way the shell does with
 * `tracking-[-0.005em]`.
 */
private val Tight = LineHeightStyle(
    alignment = LineHeightStyle.Alignment.Center,
    trim = LineHeightStyle.Trim.None,
)

private fun mh(
    size: Int,
    lineHeight: Int,
    weight: FontWeight = FontWeight.Normal,
    tracking: Double = 0.0,
) = TextStyle(
    fontFamily = FontFamily.Default,
    fontWeight = weight,
    fontSize = size.sp,
    lineHeight = lineHeight.sp,
    letterSpacing = tracking.sp,
    lineHeightStyle = Tight,
)

val MhTypography = Typography(
    // Album/track hero titles.
    headlineMedium = mh(24, 30, FontWeight.SemiBold, -0.4),
    headlineSmall = mh(20, 26, FontWeight.SemiBold, -0.3),
    titleLarge = mh(17, 22, FontWeight.SemiBold, -0.2),
    // Page toolbar title ("Albums", "All tracks") — bold, 15px on the web.
    titleMedium = mh(15, 20, FontWeight.Bold, -0.2),
    titleSmall = mh(13, 18, FontWeight.Medium),
    // Track titles in a list.
    bodyLarge = mh(15, 20),
    bodyMedium = mh(13, 18),
    // Secondary lines: artist under a title, meta rows.
    bodySmall = mh(12, 16),
    // `--text-nav: 13px` — tab labels, sidebar rows.
    labelLarge = mh(13, 16, FontWeight.Medium, -0.065),
    // `--text-nav-sm: 12.5px` — top-bar buttons, filter chips.
    labelMedium = mh(12, 15, FontWeight.Medium, -0.06),
    // `--text-nav-count: 10.5px` — counts, mobile nav labels.
    labelSmall = mh(10, 13, FontWeight.Medium),
)
