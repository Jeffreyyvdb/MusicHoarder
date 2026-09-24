package com.musichoarder.app.ui.theme

import android.app.UiModeManager
import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.compositeOver
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp

/**
 * Web parity, deliberately: **no Material You dynamic colour**. The web app has one identity — an
 * Apple-grey neutral ground with a single green tint — and picking up the wallpaper palette instead
 * would make the phone look like a different product.
 *
 * The system's contrast setting (API 34+) is the Android twin of the web's `prefers-contrast: more`
 * and picks the contrast palettes, so both clients answer the same accessibility request.
 */
@Composable
fun MusicHoarderTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val highContrast = rememberIncreasedContrast()
    val mh = when {
        darkTheme && highContrast -> MhDarkContrastColors
        darkTheme -> MhDarkColors
        highContrast -> MhLightContrastColors
        else -> MhLightColors
    }
    // The inverse slots (the snackbar) borrow the other appearance's menu surface, the way a
    // Material snackbar is dark on a light screen and light on a dark one.
    val inverse = if (darkTheme) MhLightColors else MhDarkColors

    CompositionLocalProvider(LocalMhColors provides mh) {
        MaterialTheme(
            colorScheme = materialScheme(mh, inverse),
            typography = MhTypography,
            shapes = MhShapes,
            content = content,
        )
    }
}

/**
 * Material's slots, filled from the web tokens so stock components inherit the same palette.
 *
 * The container ladder is iOS's elevation, not Material's tonal one: the page ([MhColors.background])
 * at the bottom, cells and sheets on [MhColors.card], and menus and dialogs on [MhColors.popover] —
 * which is why `surfaceContainer` (DropdownMenu's default) is the popover rather than the card.
 * `outlineVariant` is the separator, so a bare `HorizontalDivider` draws the list hairline.
 */
private fun materialScheme(mh: MhColors, inverse: MhColors): ColorScheme {
    // Material's containers must be opaque, so the web's `primary/12` tint is flattened onto the page.
    val primaryTint = mh.primary.copy(alpha = 0.12f).compositeOver(mh.background)
    return if (mh.isDark) {
        darkColorScheme(
            primary = mh.primary,
            onPrimary = mh.primaryForeground,
            primaryContainer = primaryTint,
            onPrimaryContainer = mh.primary,
            inversePrimary = inverse.primary,
            secondary = mh.secondaryForeground,
            onSecondary = mh.background,
            secondaryContainer = mh.secondary,
            onSecondaryContainer = mh.secondaryForeground,
            background = mh.background,
            onBackground = mh.foreground,
            surface = mh.background,
            onSurface = mh.foreground,
            surfaceVariant = mh.muted,
            onSurfaceVariant = mh.mutedForeground,
            inverseSurface = inverse.popover,
            inverseOnSurface = inverse.foreground,
            surfaceContainerLowest = mh.background,
            surfaceContainerLow = mh.card,
            surfaceContainer = mh.popover,
            surfaceContainerHigh = mh.popover,
            surfaceContainerHighest = mh.cardElevated,
            outline = mh.border,
            outlineVariant = mh.separator,
            error = mh.destructive,
            onError = mh.destructiveForeground,
        )
    } else {
        lightColorScheme(
            primary = mh.primary,
            onPrimary = mh.primaryForeground,
            primaryContainer = primaryTint,
            onPrimaryContainer = mh.primary,
            inversePrimary = inverse.primary,
            secondary = mh.secondaryForeground,
            onSecondary = mh.background,
            secondaryContainer = mh.secondary,
            onSecondaryContainer = mh.secondaryForeground,
            background = mh.background,
            onBackground = mh.foreground,
            surface = mh.background,
            onSurface = mh.foreground,
            surfaceVariant = mh.muted,
            onSurfaceVariant = mh.mutedForeground,
            inverseSurface = inverse.popover,
            inverseOnSurface = inverse.foreground,
            surfaceContainerLowest = mh.background,
            surfaceContainerLow = mh.card,
            surfaceContainer = mh.popover,
            surfaceContainerHigh = mh.popover,
            surfaceContainerHighest = mh.cardElevated,
            outline = mh.border,
            outlineVariant = mh.separator,
            error = mh.destructive,
            onError = mh.destructiveForeground,
        )
    }
}

/**
 * True while Settings → Accessibility → Contrast is at medium or high (API 34+), live: the listener
 * swaps the palette without an Activity restart, as the web's media query does.
 */
@Composable
private fun rememberIncreasedContrast(): Boolean {
    // SDK_INT never changes while the app runs, so this early return cannot reorder composition.
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.UPSIDE_DOWN_CAKE) return false
    val context = LocalContext.current
    val uiModeManager = remember(context) { context.getSystemService(UiModeManager::class.java) }
    var contrast by remember(uiModeManager) { mutableFloatStateOf(uiModeManager?.contrast ?: 0f) }
    DisposableEffect(uiModeManager) {
        val listener = UiModeManager.ContrastChangeListener { contrast = it }
        uiModeManager?.addContrastChangeListener(context.mainExecutor, listener)
        onDispose { uiModeManager?.removeContrastChangeListener(listener) }
    }
    return contrast >= 0.5f
}

/**
 * `--radius: 0.625rem` and the sm/md/lg/xl steps derived from it in `app.css`: 4 inline badges ·
 * 6 thumbnails · 8 grid art · 10 fields and menu items · 14 grouped sections, cards and menus.
 */
val MhShapes = Shapes(
    extraSmall = RoundedCornerShape(4.dp),
    small = RoundedCornerShape(6.dp),
    medium = RoundedCornerShape(8.dp),
    large = RoundedCornerShape(10.dp),
    extraLarge = RoundedCornerShape(14.dp),
)

/**
 * A menu's corner — 14, the web's grouped-surface radius. Material's DropdownMenu defaults to the
 * 4dp `extraSmall` corner, which reads as a different product next to the web's menus, so every
 * menu passes this instead.
 */
val MhMenuShape = RoundedCornerShape(14.dp)

/** Convenience alias so screens can read `MhTheme.colors.mutedForeground`. */
object MhTheme {
    val colors: MhColors
        @Composable get() = LocalMhColors.current
}
