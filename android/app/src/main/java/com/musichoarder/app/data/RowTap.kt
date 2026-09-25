package com.musichoarder.app.data

/**
 * What tapping a track row does. Port of the web's compact tap rule (`TrackList`'s `onActivate`),
 * shared because it is the most common tap in the app and the two clients have to agree on it.
 */
enum class RowTap {
    /** Play the list on screen from the tapped row — the list you see is the queue you get. */
    PlayFromRow,

    /**
     * The row is the song already loaded: bring the player up, resuming if it was paused. Never
     * pause it and never restart it — replaying the list from this row would throw away the
     * listener's place for a tap that almost always means "show me what is playing".
     */
    OpenPlayer,
}

/**
 * The tap rule for a row showing [tappedId] while [loadedId] is in the player.
 *
 * [sameQueueKind] guards the id comparison: a share track carries the sharing server's ids, which
 * can collide with a library id, so a library row only counts as loaded while a library queue plays
 * — and a share row only while the share queue does.
 */
fun rowTapFor(tappedId: Int, loadedId: Int?, sameQueueKind: Boolean): RowTap =
    if (sameQueueKind && tappedId == loadedId) RowTap.OpenPlayer else RowTap.PlayFromRow
