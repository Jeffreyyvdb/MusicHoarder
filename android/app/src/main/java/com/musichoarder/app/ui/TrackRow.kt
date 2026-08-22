package com.musichoarder.app.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Track
import com.musichoarder.app.ui.theme.MhTheme

/**
 * One row of the web's track table.
 *
 * At phone width the web itself renders exactly these five cells - index, cover, title block, heart,
 * duration - and hides album, year, format, size, match confidence and source behind container
 * queries, so nothing the browser shows on a phone is missing here.
 *
 * [index] is the row's position in the list rather than the tag's track number: that is what the
 * web's Tracks table numbers, and it stays continuous while filtering. Album screens pass the real
 * track number through [trackNumber] instead.
 */
@Composable
fun TrackRow(
    track: Track,
    coverUrl: String?,
    isPlaying: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    index: Int? = null,
    trackNumber: Int? = null,
    showArtwork: Boolean = true,
    isPlayingNow: Boolean = false,
    liked: Boolean = false,
    onToggleLike: (() -> Unit)? = null,
) {
    val colors = MhTheme.colors
    Row(
        modifier = modifier
            .fillMaxWidth()
            .height(56.dp)
            .clickable(onClick = onClick)
            .padding(start = 14.dp, end = if (onToggleLike != null) 6.dp else 14.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val leadingWidth = if (index != null) 34.dp else 24.dp
        Box(
            modifier = Modifier.width(leadingWidth),
            contentAlignment = Alignment.Center,
        ) {
            if (isPlaying) {
                EqualizerBars(playing = isPlayingNow)
            } else {
                Text(
                    text = index?.let { "%03d".format(it) } ?: trackNumber?.toString() ?: "-",
                    style = MaterialTheme.typography.bodySmall,
                    fontFamily = FontFamily.Monospace,
                    color = colors.mutedForeground,
                    textAlign = TextAlign.Center,
                )
            }
        }

        if (showArtwork) {
            Spacer(Modifier.size(10.dp))
            Artwork(
                url = coverUrl,
                artist = track.albumArtist,
                title = track.album,
                modifier = Modifier.size(36.dp),
            )
        }
        Spacer(Modifier.size(12.dp))

        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = track.title,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = FontWeight.Medium,
                color = if (isPlaying) colors.primary else colors.foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.height(3.dp))
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                if (track.needsReview) MhMonoBadge("REVIEW", tint = colors.destructive)
                if (track.hasLyrics) MhMonoBadge("LRC")
                Text(
                    text = track.albumArtist,
                    style = MaterialTheme.typography.bodySmall,
                    color = colors.mutedForeground,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }

        if (onToggleLike != null) {
            Spacer(Modifier.size(4.dp))
            HeartButton(liked = liked, onClick = onToggleLike)
        }

        track.durationMs?.let {
            Spacer(Modifier.size(6.dp))
            Text(
                text = formatDuration(it),
                style = MaterialTheme.typography.bodySmall,
                fontFamily = FontFamily.Monospace,
                color = colors.mutedForeground,
            )
        }
    }
}
