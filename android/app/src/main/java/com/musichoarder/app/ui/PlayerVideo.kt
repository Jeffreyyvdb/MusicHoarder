package com.musichoarder.app.ui

import android.content.Context
import android.view.Gravity
import android.view.SurfaceView
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
 * ExoPlayer renders straight into a [SurfaceView] — media3-ui's `PlayerView` would bring its own
 * controls and layout, and all this needs is the pixels. What it also needs, and what a bare
 * `SurfaceView` does not do, is *shape*: `setVideoSurfaceView` never resizes the view, so a surface
 * left at `MATCH_PARENT` stretches every clip to the phone's portrait box — a 16:9 video comes out
 * roughly 2.4x too tall. Sizing the surface from the decoded frame is what `AspectRatioFrameLayout`
 * does inside `PlayerView`, and [VideoFrameLayout] is that, minus everything else.
 *
 * [crop] picks between the web's two fits:
 * - `true` — `object-cover`, the ambient backdrop: fill the box and let the overflow be clipped.
 * - `false` — `object-contain`, the watch view: fit inside the box and letterbox the rest.
 *
 * There is no fade-in here, unlike the web's 500 ms cross-fade. A `<video>` with no first frame
 * paints nothing, so the web has to reveal it; a `SurfaceView` paints black, and alpha on a surface
 * is not reliably honoured. The surface is instead only mounted once `VideoState.isVisible` says the
 * clip is actually running, which keeps the black window down to a frame or two.
 */
@Composable
fun PlayerVideoLayer(
    aspectRatio: Float?,
    crop: Boolean,
    onAttach: (SurfaceView) -> Unit,
    onDetach: () -> Unit,
    modifier: Modifier = Modifier,
) {
    AndroidView(
        modifier = modifier,
        factory = { context ->
            VideoFrameLayout(context).apply {
                addView(
                    SurfaceView(context).also(onAttach),
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
        },
    )
    DisposableEffect(Unit) { onDispose { onDetach() } }
}

/**
 * A frame that sizes its single child to the clip's aspect ratio and centres it, clipping whatever
 * hangs over the edge. `clipChildren` is on by default, and a `SurfaceView` *is* clipped by its
 * parent view's bounds — which is why the overflow has to happen inside a real `ViewGroup` rather
 * than under a Compose `clipToBounds`, whose canvas clip a surface would ignore.
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

    override fun onMeasure(widthMeasureSpec: Int, heightMeasureSpec: Int) {
        val width = MeasureSpec.getSize(widthMeasureSpec)
        val height = MeasureSpec.getSize(heightMeasureSpec)
        setMeasuredDimension(width, height)

        val (childWidth, childHeight) = videoChildSize(width, height, aspectRatio, crop)
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
 * An unknown ratio fills the box, which is the old always-stretch behaviour — but only for the
 * moment before the decoder reports a size, rather than forever.
 */
internal fun videoChildSize(
    boxWidth: Int,
    boxHeight: Int,
    ratio: Float,
    crop: Boolean,
): Pair<Int, Int> {
    if (ratio <= 0f || boxWidth <= 0 || boxHeight <= 0) return boxWidth to boxHeight
    val boxRatio = boxWidth.toFloat() / boxHeight
    // Cropping means the *other* axis overflows, so the two modes pick opposite sides of the
    // comparison — a clip wider than the box fills by height when cropping, by width when fitting.
    val matchWidth = if (crop) ratio <= boxRatio else ratio >= boxRatio
    return if (matchWidth) {
        boxWidth to (boxWidth / ratio).roundToInt()
    } else {
        (boxHeight * ratio).roundToInt() to boxHeight
    }
}
