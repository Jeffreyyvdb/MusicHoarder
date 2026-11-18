import AppIntents
import SwiftUI
import UIKit
import WidgetBridge
import WidgetKit

/// The song on this phone, with its controls. The app reloads it whenever the audio engine
/// publishes a different snapshot, so its timeline never asks for a refresh of its own.
struct NowPlayingWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: WidgetKind.nowPlaying, provider: NowPlayingProvider()) { entry in
            NowPlayingView(entry: entry)
                .containerBackground(Color.widgetSurface, for: .widget)
        }
        .configurationDisplayName("Now Playing")
        .description("The song that is playing, with its controls.")
        .supportedFamilies([.systemSmall, .systemMedium])
    }
}

struct NowPlayingEntry: TimelineEntry {
    let date: Date
    let nowPlaying: NowPlaying
    /// Decoded when the entry is made: a view has no time to read a file.
    let cover: UIImage?
    /// False when this build has no App Group, so nothing the app does can reach the widget.
    let hasSharedFolder: Bool

    /// What the widget gallery shows before anything has been played.
    static let sample = NowPlayingEntry(
        date: Date(),
        nowPlaying: .track(
            TrackInfo(title: "Song title", artist: "Artist", album: "Album", art: nil),
            .background(Transport(playing: false, position: 42, duration: 180, rate: 1,
                                  asOf: Date(), canNext: true, canPrevious: true))),
        cover: nil,
        hasSharedFolder: true)
}

struct NowPlayingProvider: TimelineProvider {
    func placeholder(in context: Context) -> NowPlayingEntry {
        .sample
    }

    func getSnapshot(in context: Context, completion: @escaping (NowPlayingEntry) -> Void) {
        let now = entries().first ?? .sample
        if context.isPreview, case .nothing = now.nowPlaying {
            completion(.sample)
        } else {
            completion(now)
        }
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<NowPlayingEntry>) -> Void) {
        // `.never`: the moments this widget changes by itself are already entries.
        completion(Timeline(entries: entries(), policy: .never))
    }

    private func entries() -> [NowPlayingEntry] {
        let store = WidgetStore.forThisBundle()
        let snapshot = store?.readNowPlaying() ?? .nothing
        var cover: UIImage?
        if case .track(let info, _) = snapshot {
            cover = store?.cover(info.art, side: 160)
        }
        return NowPlayingTimeline.entries(for: snapshot, now: Date()).map { entry in
            NowPlayingEntry(date: entry.date, nowPlaying: entry.nowPlaying, cover: cover,
                            hasSharedFolder: store != nil)
        }
    }
}

struct NowPlayingView: View {
    @Environment(\.widgetFamily) private var family
    let entry: NowPlayingEntry

    var body: some View {
        switch entry.nowPlaying {
        case .nothing:
            if entry.hasSharedFolder {
                WidgetNotice(title: "Nothing playing", detail: "Open MusicHoarder to play something.")
            } else {
                WidgetNotice(title: "Open MusicHoarder")
            }
        case .track(let info, let reach):
            if family == .systemSmall {
                SmallNowPlaying(info: info, reach: reach, cover: entry.cover)
                    // A small widget takes one link. Without a player to reach it is the Play
                    // glyph's, since that is what a finger aims for.
                    .widgetURL(reach == .needsApp ? WidgetLink.resume.url : WidgetLink.nowPlaying.url)
            } else {
                MediumNowPlaying(info: info, reach: reach, cover: entry.cover)
                    .widgetURL(WidgetLink.nowPlaying.url)
            }
        }
    }
}

/// Cover top left, the app's mark top right, the words and a round Play or Pause along the bottom.
private struct SmallNowPlaying: View {
    let info: TrackInfo
    let reach: Reach
    let cover: UIImage?

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top, spacing: 8) {
                CoverView(image: cover)
                    // Before the spacer, or the two would halve the width between them.
                    .layoutPriority(1)
                Spacer(minLength: 0)
                AppGlyph()
            }
            HStack(alignment: .center, spacing: 6) {
                TrackWords(info: info, titleSize: 13, detailSize: 12, showsAlbum: false)
                Spacer(minLength: 0)
                PlayPause(reach: reach, size: 38, linksWhenUnreachable: false)
            }
        }
    }
}

/// Cover on the left; the words, the song's progress and the three transport buttons beside it.
private struct MediumNowPlaying: View {
    let info: TrackInfo
    let reach: Reach
    let cover: UIImage?

    var body: some View {
        HStack(alignment: .center, spacing: 14) {
            CoverView(image: cover, cornerRadius: 10)
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top, spacing: 8) {
                    TrackWords(info: info, titleSize: 15, detailSize: 13, showsAlbum: true)
                    Spacer(minLength: 0)
                    AppGlyph()
                }
                Spacer(minLength: 6)
                SongProgress(reach: reach)
                Spacer(minLength: 6)
                controls
            }
        }
    }

    @ViewBuilder private var controls: some View {
        switch reach {
        case .background(let transport):
            HStack(spacing: 0) {
                SkipButton(command: .previous, enabled: transport.canPrevious)
                Spacer(minLength: 0)
                PlayPause(reach: reach, size: 40, linksWhenUnreachable: true)
                Spacer(minLength: 0)
                SkipButton(command: .next, enabled: transport.canNext)
            }
        case .needsApp:
            // Only Play is offered: it opens the app, and Next there is one tap further.
            HStack(spacing: 0) {
                Spacer(minLength: 0)
                PlayPause(reach: reach, size: 40, linksWhenUnreachable: true)
                Spacer(minLength: 0)
            }
        }
    }
}

private struct TrackWords: View {
    let info: TrackInfo
    let titleSize: CGFloat
    let detailSize: CGFloat
    let showsAlbum: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(info.title)
                .font(.system(size: titleSize, weight: .bold))
                .foregroundStyle(.primary)
                .widgetAccentable()
            Text(info.artist)
                .font(.system(size: detailSize))
                .foregroundStyle(.secondary)
            if showsAlbum, !info.album.isEmpty {
                Text(info.album)
                    .font(.system(size: detailSize - 1))
                    .foregroundStyle(.secondary)
            }
        }
        .lineLimit(1)
    }
}

/// The round button. With a player to reach it runs in the app's process, with the app left in
/// the background, and it carries what its glyph shows: a press on a widget that has gone stale
/// can then never do the opposite. Without one it opens the app, which resumes.
private struct PlayPause: View {
    let reach: Reach
    let size: CGFloat
    /// False in the small widget, where the widget's own link does the opening.
    let linksWhenUnreachable: Bool

    var body: some View {
        switch reach {
        case .background(let transport):
            Button(intent: PlaybackCommandIntent(transport.playing ? .pause : .play)) {
                glyph(transport.playing ? "pause.circle.fill" : "play.circle.fill")
            }
            .buttonStyle(.plain)
            .accessibilityLabel(transport.playing ? "Pause" : "Play")
        case .needsApp:
            if linksWhenUnreachable {
                Link(destination: WidgetLink.resume.url) { glyph("play.circle.fill") }
                    .accessibilityLabel("Play")
            } else {
                glyph("play.circle.fill")
                    .accessibilityLabel("Play")
            }
        }
    }

    /// One symbol in one colour: the triangle is a hole in the disc, so the button reads the
    /// same in full colour, tinted and clear.
    private func glyph(_ name: String) -> some View {
        Image(systemName: name)
            .resizable()
            .scaledToFit()
            .symbolRenderingMode(.monochrome)
            .foregroundStyle(.primary)
            .frame(width: size, height: size)
            .widgetAccentable()
    }
}

private struct SkipButton: View {
    let command: WidgetCommand
    let enabled: Bool

    var body: some View {
        Button(intent: PlaybackCommandIntent(command)) {
            Image(systemName: command == .next ? "forward.fill" : "backward.fill")
                .font(.system(size: 19, weight: .semibold))
                .foregroundStyle(.primary)
                .frame(width: 48, height: 40)
                .contentShape(Rectangle())
                .widgetAccentable()
        }
        .buttonStyle(.plain)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0.35)
        .accessibilityLabel(command == .next ? "Next track" : "Previous track")
    }
}

/// How far the song is. While it plays the system moves the bar itself, from the moment the
/// snapshot was written: no timeline entry per second, and none needed.
private struct SongProgress: View {
    let reach: Reach

    var body: some View {
        if case .background(let transport) = reach, transport.duration > 0, transport.rate > 0 {
            if transport.playing {
                let start = transport.asOf.addingTimeInterval(-transport.position / transport.rate)
                let end = start.addingTimeInterval(transport.duration / transport.rate)
                ProgressView(timerInterval: start...end, countsDown: false) {
                    EmptyView()
                } currentValueLabel: {
                    EmptyView()
                }
                .tint(Color.primary)
                .widgetAccentable()
            } else {
                ProgressView(value: min(transport.position, transport.duration),
                             total: transport.duration)
                    .tint(Color.primary)
                    .widgetAccentable()
            }
        }
    }
}
