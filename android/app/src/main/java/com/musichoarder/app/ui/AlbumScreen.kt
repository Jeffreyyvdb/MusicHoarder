package com.musichoarder.app.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.musichoarder.app.data.Album
import com.musichoarder.app.data.Track
import com.musichoarder.app.data.likedNow
import com.musichoarder.app.ui.theme.MhTheme

/**
 * One album, in track order — the web's compact album page: a top bar with Back, a centred hero
 * (cover, title, the artist as a link, a short meta line, Play / Shuffle), the tracklist, and the
 * Apple Music summary line under it.
 */
@Composable
fun AlbumScreen(
    album: Album,
    /** "Shared by X" for an album from someone else's library; null when this account owns it. */
    sharedBy: String? = null,
    coverUrl: (Track, Int) -> String?,
    playingTrackId: Int?,
    likes: Map<Int, String?>,
    onToggleLike: (Track) -> Unit,
    /** The Play button: the album from the top. */
    onPlay: (List<Track>, Int) -> Unit,
    onShuffle: (List<Track>) -> Unit,
    onBack: () -> Unit,
    contentPadding: PaddingValues,
    modifier: Modifier = Modifier,
    /** A row tap, which follows the row tap rule (see `RowTap`) rather than always playing. */
    onActivateRow: (List<Track>, Int) -> Unit = onPlay,
    /** A row's "Go to artist", by lead-artist name; null leaves it out of the menu. */
    onOpenArtist: ((String) -> Unit)? = null,
    isPlayingNow: Boolean = false,
    /**
     * Pause / resume, for when one of this album's tracks is the loaded song: Play then speaks for
     * that song — "Pause" while it plays, resuming (never restarting) when it does not — the web's
     * album Play. Null keeps Play as "from the top" always.
     */
    onPlayPause: (() -> Unit)? = null,
    /**
     * "Add to playlist…" for [tracks] under [label] — a row's track, or the whole album from the
     * bar's ⋮ (the web album page's menu). Null leaves both out.
     */
    onAddToPlaylist: ((tracks: List<Track>, label: String) -> Unit)? = null,
) {
    val colors = MhTheme.colors
    val cover = album.tracks.firstOrNull { it.hasCover }?.let { coverUrl(it, 640) }
    val listState = rememberLazyListState()
    val loadedHere = playingTrackId != null && album.tracks.any { it.id == playingTrackId }
    val playPause = onPlayPause?.takeIf { loadedHere }

    Column(modifier = modifier.fillMaxSize().background(colors.background)) {
        DrillInTopBar(
            title = album.name,
            listState = listState,
            navigationIcon = Icons.AutoMirrored.Rounded.ArrowBack,
            navigationLabel = "Back",
            onNavigate = onBack,
            actions = {
                if (onAddToPlaylist != null && album.tracks.isNotEmpty()) {
                    MhMenuButton(Icons.Rounded.MoreVert, "More options for ${album.name}") { close ->
                        MhMenuActionItem("Add to playlist…") {
                            close()
                            onAddToPlaylist(album.tracks, album.name)
                        }
                    }
                }
            },
        )

        LazyColumn(state = listState, modifier = Modifier.fillMaxSize(), contentPadding = contentPadding) {
            item(key = "hero", contentType = "hero") {
                AlbumHero(
                    coverUrl = cover,
                    artist = album.artist,
                    title = album.name,
                    // The lead artist: the name the Artists page files this album under.
                    onOpenArtist = onOpenArtist?.let { open -> { open(album.artist) } },
                    metaLines = listOfNotNull(
                        album.year?.toString(),
                        // An album belongs to one grantor, so the page says it once, up here.
                        sharedBy,
                        // This card folds several destination folders together - say so, rather
                        // than silently hiding that the album is split on disk.
                        if (album.folderKeys.size > 1) "${album.folderKeys.size} editions merged" else null,
                    ),
                    onPlay = playPause ?: { onPlay(album.tracks, 0) },
                    playLabel = if (playPause != null && isPlayingNow) "Pause" else "Play",
                    playIcon = if (playPause != null && isPlayingNow) Icons.Rounded.Pause else Icons.Rounded.PlayArrow,
                    onShuffle = { onShuffle(album.tracks) },
                )
            }

            itemsIndexed(album.tracks, key = { _, track -> track.id }) { index, track ->
                TrackRow(
                    track = track,
                    coverUrl = null,
                    isPlaying = track.id == playingTrackId,
                    isPlayingNow = isPlayingNow,
                    liked = likedNow(likes, track),
                    onToggleLike = { onToggleLike(track) },
                    onClick = { onActivateRow(album.tracks, index) },
                    trackNumber = track.trackNumber,
                    // The cover is already the header — repeating it 12 times adds nothing.
                    showArtwork = false,
                    // The album header already names the grantor, so the rows only need the mark.
                    sharedBy = sharedBy,
                    // No "Go to album": this is it. The artist link is the lead artist, the name the
                    // Artists page files the album under.
                    onOpenArtist = onOpenArtist?.let { open -> { open(track.albumArtist) } },
                    onAddToPlaylist = onAddToPlaylist?.let { add -> { add(listOf(track), track.title) } },
                )
                if (index < album.tracks.lastIndex) TrackRowSeparator(showArtwork = false)
            }

            item(key = "summary", contentType = "footer") {
                TracklistSummary(album.tracks)
            }
        }
    }
}

/**
 * The top bar of a drill-in (an album, a share): the navigation button, and the page's title,
 * which fades in only once the hero that already shows it has scrolled away — the web's collapsing
 * nav bar, and the Material top app bar's scrolled state, with its hairline.
 */
@Composable
fun DrillInTopBar(
    title: String,
    listState: LazyListState,
    navigationIcon: ImageVector,
    navigationLabel: String,
    onNavigate: () -> Unit,
    alwaysShowTitle: Boolean = false,
    /** How far into the first item the hero's own title sits — past it, the bar takes over. */
    titleAfter: Dp = 290.dp,
    /** Trailing buttons (a playlist's ⋮); none on an album or a share. */
    actions: @Composable RowScope.() -> Unit = {},
) {
    val colors = MhTheme.colors
    val titlePx = with(LocalDensity.current) { titleAfter.roundToPx() }
    val scrolled by remember(listState, titlePx) {
        derivedStateOf {
            listState.firstVisibleItemIndex > 0 || listState.firstVisibleItemScrollOffset > titlePx
        }
    }
    Column(modifier = Modifier.fillMaxWidth().statusBarsPadding()) {
        Row(
            modifier = Modifier.fillMaxWidth().heightIn(min = 56.dp).padding(horizontal = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            IconButton(onClick = onNavigate) {
                Icon(navigationIcon, contentDescription = navigationLabel, tint = colors.foreground)
            }
            // The title's slot keeps its width while the title is hidden, so trailing actions stay
            // at the end rather than sliding up against the back button.
            Box(modifier = Modifier.weight(1f)) {
                // The plain overload: inside the Box, the Row's scoped one would be picked up.
                androidx.compose.animation.AnimatedVisibility(
                    visible = alwaysShowTitle || scrolled,
                    enter = fadeIn(tween(150)),
                    exit = fadeOut(tween(150)),
                ) {
                    Text(
                        title,
                        style = MaterialTheme.typography.titleLarge,
                        color = colors.foreground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.padding(start = 12.dp, end = 16.dp),
                    )
                }
            }
            actions()
        }
        val hairline = with(LocalDensity.current) { 1f.toDp() }
        HorizontalDivider(
            thickness = hairline,
            color = if (scrolled) colors.separator else Color.Transparent,
        )
    }
}

/**
 * The album hero, shared by the album page and the share viewer so the two cannot drift: a 240dp
 * cover with a soft shadow, the title (`text-title-2`, centred), the artist — a tint link when it
 * leads somewhere, plain text when it does not — the muted meta lines, and Play / Shuffle.
 */
@Composable
fun AlbumHero(
    coverUrl: String?,
    artist: String,
    title: String,
    onOpenArtist: (() -> Unit)?,
    metaLines: List<String>,
    onPlay: () -> Unit,
    onShuffle: (() -> Unit)?,
    playLabel: String = "Play",
    playIcon: ImageVector = Icons.Rounded.PlayArrow,
    /** Draws the cover instead of [coverUrl] (a playlist's mosaic). Given the hero's size and shape. */
    artwork: (@Composable (Modifier, Shape) -> Unit)? = null,
    /** Hides Play / Shuffle (an empty playlist has nothing to play). */
    showPlayButtons: Boolean = true,
) {
    val colors = MhTheme.colors
    Column(
        modifier = Modifier.fillMaxWidth().padding(top = 8.dp, bottom = 16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        BoxWithConstraints(modifier = Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
            // 240dp, or less on a very narrow window, so the cover always keeps its margins.
            val side = minOf(240.dp, maxWidth - 64.dp)
            Box(
                modifier = Modifier
                    .size(side)
                    .shadow(
                        elevation = 16.dp,
                        shape = HeroShape,
                        ambientColor = Color.Black.copy(alpha = 0.28f),
                        spotColor = Color.Black.copy(alpha = 0.28f),
                    ),
            ) {
                if (artwork != null) {
                    artwork(Modifier.fillMaxSize(), HeroShape)
                } else {
                    Artwork(
                        url = coverUrl,
                        artist = artist,
                        title = title,
                        modifier = Modifier.fillMaxSize(),
                        shape = HeroShape,
                    )
                }
            }
        }
        Spacer(Modifier.height(16.dp))
        Text(
            title,
            style = MaterialTheme.typography.headlineMedium,
            color = colors.foreground,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(horizontal = 24.dp).semantics { heading() },
        )
        Text(
            artist,
            style = MaterialTheme.typography.headlineSmall,
            fontWeight = FontWeight.Normal,
            // The tint is spent on things you can tap: a link here, plain text where the artist
            // leads nowhere (a share, which has no library to go to).
            color = if (onOpenArtist != null) colors.primary else colors.foreground,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier
                .padding(horizontal = 24.dp)
                .clip(RoundedCornerShape(6.dp))
                .then(
                    if (onOpenArtist != null) {
                        Modifier.clickable(onClickLabel = "Go to artist", role = Role.Button, onClick = onOpenArtist)
                    } else {
                        Modifier
                    }
                )
                .padding(horizontal = 4.dp, vertical = 2.dp),
        )
        for (line in metaLines) {
            Text(
                line,
                style = MaterialTheme.typography.bodyMedium,
                color = colors.mutedForeground,
                textAlign = TextAlign.Center,
                modifier = Modifier.padding(horizontal = 24.dp),
            )
        }
        if (showPlayButtons) {
            Spacer(Modifier.height(20.dp))
            MhPlayShufflePills(onPlay = onPlay, onShuffle = onShuffle, playLabel = playLabel, playIcon = playIcon)
        }
    }
}

private val HeroShape = RoundedCornerShape(10.dp)

/**
 * "12 tracks · 52 min" under the tracklist — the web album page's footer line, in the app's one
 * word for them ("tracks", as the tab says).
 */
@Composable
fun TracklistSummary(tracks: List<Track>) {
    val seconds = remember(tracks) { tracks.sumOf { it.durationSeconds.toLong() } }
    val count = "${tracks.size} track${if (tracks.size == 1) "" else "s"}"
    Text(
        if (seconds > 0) "$count · ${formatTotalDuration(seconds)}" else count,
        style = MaterialTheme.typography.bodyMedium.copy(fontFeatureSettings = "tnum"),
        color = MhTheme.colors.mutedForeground,
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 16.dp),
    )
}

/** The web's primary / outline button pair, rounded-full. The invite screen's Accept / Not now. */
@Composable
fun PillButton(
    label: String,
    icon: ImageVector,
    filled: Boolean,
    onClick: () -> Unit,
) {
    val colors = MhTheme.colors
    Row(
        modifier = Modifier
            .clip(CircleShape)
            .background(if (filled) colors.primary else Color.Transparent)
            .border(1.dp, if (filled) colors.primary else colors.border, CircleShape)
            .clickable(onClick = onClick)
            .padding(horizontal = 20.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            icon,
            contentDescription = null,
            tint = if (filled) colors.primaryForeground else colors.foreground,
            modifier = Modifier.size(17.dp),
        )
        Spacer(Modifier.size(7.dp))
        Text(
            label,
            style = MaterialTheme.typography.labelLarge,
            fontWeight = FontWeight.SemiBold,
            color = if (filled) colors.primaryForeground else colors.foreground,
        )
    }
}
