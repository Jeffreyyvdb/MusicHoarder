package com.musichoarder.app.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.unit.dp

/**
 * Web parity, deliberately: **no Material You dynamic colour**. The web app has one identity — a
 * near-black neutral ground with a single green accent — and picking up the wallpaper palette
 * instead would make the phone look like a different product.
 */
@Composable
fun MusicHoarderTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit,
) {
    val mh = if (darkTheme) MhDarkColors else MhLightColors

    // Material's slots, filled from the web tokens so stock components inherit the same palette.
    // `surface` is the page ground and `surfaceContainer*` climb toward the raised card colours,
    // which is how the CSS treats background → card → popover.
    val scheme = if (darkTheme) {
        darkColorScheme(
            primary = mh.primary,
            onPrimary = mh.primaryForeground,
            primaryContainer = mh.accent,
            onPrimaryContainer = mh.accentForeground,
            secondary = mh.secondaryForeground,
            onSecondary = mh.secondary,
            secondaryContainer = mh.secondary,
            onSecondaryContainer = mh.secondaryForeground,
            background = mh.background,
            onBackground = mh.foreground,
            surface = mh.background,
            onSurface = mh.foreground,
            surfaceVariant = mh.muted,
            onSurfaceVariant = mh.mutedForeground,
            surfaceContainerLowest = mh.surfaceSunken,
            surfaceContainerLow = mh.background,
            surfaceContainer = mh.card,
            surfaceContainerHigh = mh.popover,
            surfaceContainerHighest = mh.secondary,
            outline = mh.border,
            outlineVariant = mh.border,
            error = mh.destructive,
            onError = mh.destructiveForeground,
        )
    } else {
        lightColorScheme(
            primary = mh.primary,
            onPrimary = mh.primaryForeground,
            primaryContainer = mh.accent,
            onPrimaryContainer = mh.accentForeground,
            secondary = mh.secondaryForeground,
            onSecondary = mh.secondary,
            secondaryContainer = mh.secondary,
            onSecondaryContainer = mh.secondaryForeground,
            background = mh.background,
            onBackground = mh.foreground,
            surface = mh.background,
            onSurface = mh.foreground,
            surfaceVariant = mh.muted,
            onSurfaceVariant = mh.mutedForeground,
            surfaceContainerLowest = mh.surfaceSunken,
            surfaceContainerLow = mh.background,
            surfaceContainer = mh.card,
            surfaceContainerHigh = mh.popover,
            surfaceContainerHighest = mh.secondary,
            outline = mh.border,
            outlineVariant = mh.border,
            error = mh.destructive,
            onError = mh.destructiveForeground,
        )
    }

    CompositionLocalProvider(LocalMhColors provides mh) {
        MaterialTheme(
            colorScheme = scheme,
            typography = MhTypography,
            shapes = MhShapes,
            content = content,
        )
    }
}

/** `--radius: 0.5rem` and the sm/md/lg/xl steps derived from it in `app.css`. */
val MhShapes = Shapes(
    extraSmall = RoundedCornerShape(4.dp),
    small = RoundedCornerShape(6.dp),
    medium = RoundedCornerShape(8.dp),
    large = RoundedCornerShape(12.dp),
    extraLarge = RoundedCornerShape(16.dp),
)

/** The floating chrome (mini player, nav pill) is `rounded-2xl` on the web. */
val MhFloatingShape = RoundedCornerShape(16.dp)

/** Convenience alias so screens can read `MhTheme.colors.mutedForeground`. */
object MhTheme {
    val colors: MhColors
        @Composable get() = LocalMhColors.current
}
