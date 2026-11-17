import SwiftUI
import UIKit
import WidgetBridge
import WidgetKit

/// The Overview's "Last played" shelf, as the page last handed it to the app. It changes only
/// while the app runs: the widget holds no credential, so it cannot ask the server itself.
struct LastPlayedWidget: Widget {
    var body: some WidgetConfiguration {
        StaticConfiguration(kind: WidgetKind.lastPlayed, provider: LastPlayedProvider()) { entry in
            LastPlayedView(entry: entry)
                .containerBackground(Color.widgetSurface, for: .widget)
        }
        .configurationDisplayName("Last played")
        .description("The albums you played most recently.")
        .supportedFamilies([.systemMedium, .systemLarge])
    }
}

struct LastPlayedEntry: TimelineEntry {
    struct Tile: Identifiable {
        let item: ShelfItem
        let cover: UIImage?
        var id: String { item.id }
    }

    let date: Date
    let tiles: [Tile]
}

struct LastPlayedProvider: TimelineProvider {
    func placeholder(in context: Context) -> LastPlayedEntry {
        LastPlayedEntry(date: Date(), tiles: [])
    }

    func getSnapshot(in context: Context, completion: @escaping (LastPlayedEntry) -> Void) {
        completion(entry())
    }

    func getTimeline(in context: Context, completion: @escaping (Timeline<LastPlayedEntry>) -> Void) {
        completion(Timeline(entries: [entry()], policy: .never))
    }

    private func entry() -> LastPlayedEntry {
        let store = WidgetStore.forThisBundle()
        let shelf = store?.readShelf() ?? .empty
        return LastPlayedEntry(date: Date(), tiles: shelf.items.map { item in
            LastPlayedEntry.Tile(item: item, cover: store?.cover(item.art, side: 80))
        })
    }
}

struct LastPlayedView: View {
    private static let columns = 4

    @Environment(\.widgetFamily) private var family
    let entry: LastPlayedEntry

    var body: some View {
        if entry.tiles.isEmpty {
            WidgetNotice(title: "Last played", detail: "Play something in MusicHoarder.")
        } else {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .center, spacing: 8) {
                    Text("Last played")
                        .font(.system(size: 13, weight: .bold))
                        .foregroundStyle(.primary)
                        .widgetAccentable()
                    Spacer(minLength: 0)
                    AppGlyph(size: 18)
                }
                ForEach(rows.indices, id: \.self) { index in
                    Spacer(minLength: 8)
                    row(rows[index])
                }
                if family == .systemLarge {
                    Spacer(minLength: 0)
                }
            }
        }
    }

    /// One row of four in the medium widget, two in the large one.
    private var rows: [[LastPlayedEntry.Tile]] {
        let shown = Array(entry.tiles.prefix(family == .systemLarge ? 2 * Self.columns : Self.columns))
        return stride(from: 0, to: shown.count, by: Self.columns).map { start in
            Array(shown[start..<min(start + Self.columns, shown.count)])
        }
    }

    private func row(_ tiles: [LastPlayedEntry.Tile]) -> some View {
        HStack(alignment: .top, spacing: 10) {
            ForEach(0..<Self.columns, id: \.self) { column in
                if column < tiles.count {
                    ShelfTile(tile: tiles[column], detailed: family == .systemLarge)
                } else {
                    // A short row keeps its columns, so three albums are not drawn wider than four.
                    Color.clear.frame(maxWidth: .infinity, maxHeight: 0)
                }
            }
        }
    }
}

/// One album: its cover and its name, and in the large widget the artist. A tap opens the app on
/// it. The link names the item's id and nothing else; the app looks up where that leads in the
/// shelf its own page published.
private struct ShelfTile: View {
    let tile: LastPlayedEntry.Tile
    let detailed: Bool

    var body: some View {
        Link(destination: WidgetLink.shelfItem(id: tile.item.id).url) {
            VStack(alignment: .leading, spacing: 4) {
                CoverView(image: tile.cover, cornerRadius: 6)
                Text(tile.item.title)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(.primary)
                    .lineLimit(detailed ? 2 : 1, reservesSpace: true)
                    .widgetAccentable()
                if detailed {
                    Text(tile.item.subtitle)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .accessibilityLabel(tile.item.subtitle.isEmpty
            ? tile.item.title : "\(tile.item.title), \(tile.item.subtitle)")
    }
}
