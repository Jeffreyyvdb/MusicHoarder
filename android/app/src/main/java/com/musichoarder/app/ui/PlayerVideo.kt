package com.musichoarder.app.ui

import android.content.Context
import android.view.Gravity
import android.view.TextureView
import android.view.ViewGroup
import android.widget.FrameLayout
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import kotlin.math.roundToInt

/**
 * The video output.
 *
 * ExoPlayer renders straight into a [TextureView] — media3-ui's `PlayerView` would bring its own
 * controls and layout, and all this needs is the pixels. What it also needs, and what neither view
 * does on its own, is *shape*: ExoPlayer never resizes the view it is given, so one left at
 * `MATCH_PARENT` stretches every clip to the phone's portrait box — a 16:9 video comes out roughly
 * 2.4x too tall. Sizing it from the decoded frame is what `AspectRatioFrameLayout` does inside
 * `PlayerView`, and [VideoFrameLayout] is that, minus everything else.
 *
 * [crop] picks between the web's two fits:
 * - `true` — `object-cover`, the ambient backdrop: fill the box and let the overflow be clipped.
 *   The box is filled with the clip's *picture*: [letterbox] and [pillarbox] are the black bars
 *   baked into its frame (from the video info), and they are pushed outside the box with the rest
 *   of the overflow, as the web's `cropMatte` does. Otherwise a film mastered into 16:9 paints two
 *   solid bands across the player.
 * - `false` — `object-contain`, the watch view: fit inside the box and letterbox the rest.
 *
 * There is no fade-in here, unlike the web's 500 ms cross-fade: the view is only mounted once
 * `VideoState.isVisible` says the clip is actually running, which keeps the blank window before the
 * first frame down to a frame or two.
 */
@Composable
fun PlayerVideoLayer(
    aspectRatio: Float?,
    crop: Boolean,
    letterbox: Float,
    pillarbox: Float,
    onAttach: (TextureView) -> Unit,
    onDetach: () -> Unit,
    modifier: Modifier = Modifier,
) {
    AndroidView(
        modifier = modifier,
        factory = { context ->
            VideoFrameLayout(context).apply {
                addView(
                    TextureView(context).also(onAttach),
                    FrameLayout.LayoutParams(
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        ViewGroup.LayoutParams.MATCH_PARENT,
                        Gravity.CENTER,
                    ),
                )
            }
        },
        update = { frame ->
            frame.aspectRatio = aspectRatio ?: 0f
            frame.crop = crop
            frame.letterbox = letterbox
            frame.pillarbox = pillarbox
        },
    )
    DisposableEffect(Unit) { onDispose { onDetach() } }
}

/**
 * A frame that sizes its single child to the clip's aspect ratio and centres it, clipping whatever
 * hangs over the edge. `clipChildren` is on by default, so the crop mode's overflow is trimmed by
 * the frame rather than spilling across the screen.
 */
internal class VideoFrameLayout(context: Context) : FrameLayout(context) {
    /** Width / height of the decoded frame; 0 means "not known yet" and fills the box. */
    var aspectRatio: Float = 0f
        set(value) {
            if (field != value) {
                field = value
                requestLayout()
            }
        }

    var crop: Boolean = true
        set(value) {
            if (field != value) {
                field = value
                requestLayout()
            }
        }

    /** Share of the frame each baked-in bar covers, top and bottom; only the crop mode uses it. */
    var letterbox: Float = 0f
        set(value) {
            if (field != value) {
                field = value
                requestLayout()
            }
        }

    /** Share of the frame each baked-in bar covers, at the sides; only the crop mode uses it. */
    var pillarbox: Float = 0f
        set(value) {
            if (field != value) {
                field = value
                requestLayout()
            }
        }

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        val height = MeasureSpec.getSize(heightMeasureSpec)
        setMeasuredDimension(width, height)

        val (childWidth, childHeight) = videoChildSize(width, height, aspectRatio, crop, letterbox, pillarbox)
        for (i in 0 until childCount) {
            getChildAt(i).measure(
                MeasureSpec.makeMeasureSpec(childWidth, MeasureSpec.EXACTLY),
                MeasureSpec.makeMeasureSpec(childHeight, MeasureSpec.EXACTLY),
            )
        }
    }
}

/**
 * The size a clip of [ratio] (width / height) has to take inside a [boxWidth] x [boxHeight] frame
 * to fill it ([crop], the web's `object-cover`) or to fit inside it (the web's `object-contain`).
 *
 * When cropping, it is the clip's picture that fills the box: [letterbox] and [pillarbox] are the
 * share of the frame each baked-in bar covers (top and bottom, then the sides), and the frame grows
 * until the bars hang outside the box — the web's `matteScale` in crop-matte.ts, pinned case for
 * case by the same tests. Bars already outside the box (a pillarbox on a portrait phone) cost
 * nothing. Fitting ignores them: the watch view shows the frame as it was mastered.
 *
 * An unknown ratio fills the box, which is the old always-stretch behaviour — but only for the
 * moment before the decoder reports a size, rather than forever.
 */
internal fun videoChildSize(
    boxWidth: Int,
    boxHeight: Int,
    ratio: Float,
    crop: Boolean,
    letterbox: Float = 0f,
    pillarbox: Float = 0f,
): Pair<Int, Int> {
    if (ratio <= 0f || boxWidth <= 0 || boxHeight <= 0) return boxWidth to boxHeight
    val boxRatio = boxWidth.toFloat() / boxHeight
    if (crop) {
        // The share of each axis that is picture. Cropping means the *other* axis overflows, so a
        // picture wider than the box fills it by height.
        val keepX = 1f - 2f * barShare(pillarbox)
        val keepY = 1f - 2f * barShare(letterbox)
        return if (ratio * keepX / keepY <= boxRatio) {
            val width = boxWidth / keepX
            width.roundToInt() to (width / ratio).roundToInt()
        } else {
            val height = boxHeight / keepY
            (height * ratio).roundToInt() to height.roundToInt()
        }
    }
    return if (ratio >= boxRatio) {
        boxWidth to (boxWidth / ratio).roundToInt()
    } else {
        (boxHeight * ratio).roundToInt() to boxHeight
    }
}

/** A bar pair has to leave some picture between it; anything else is not a measurement. */
private fun barShare(value: Float): Float =
    if (value.isFinite() && value > 0f && value < 0.5f) value else 0f
