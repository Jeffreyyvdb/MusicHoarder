import Foundation

/// What the Now Playing widget shows over time, as a pure function of the stored snapshot and the
/// clock. The widget stops offering a background button by itself: no process has to be alive to
/// tell it that the app has since been suspended for good or killed.
public enum NowPlayingTimeline {
    /// How long a paused player is trusted to still be in memory.
    public static let pausedReach: TimeInterval = 30 * 60
    /// How long past a song's expected end a "playing" snapshot is believed without news. The
    /// engine publishes every track change, so silence this long means it is gone.
    public static let overrun: TimeInterval = 90

    public struct Entry: Equatable {
        public let date: Date
        public let nowPlaying: NowPlaying
    }

    public static func entries(for snapshot: NowPlaying, now: Date) -> [Entry] {
        guard case .track(_, .background(let transport)) = snapshot else {
            return [Entry(date: now, nowPlaying: snapshot)]
        }
        // A song whose length is not known yet is given the paused player's allowance.
        let lapse = transport.expectedEnd?.addingTimeInterval(overrun)
            ?? transport.asOf.addingTimeInterval(pausedReach)
        if lapse <= now {
            return [Entry(date: now, nowPlaying: snapshot.demoted)]
        }
        return [
            Entry(date: now, nowPlaying: snapshot),
            Entry(date: lapse, nowPlaying: snapshot.demoted)
        ]
    }
}
