import Foundation

// What the Home Screen widgets draw, and its form on disk. Foundation only: this module is linked
// by the plugin, the app target and the widget extension, so it must stay safe for all three (no
// UIKit, no WidgetKit, no Capacitor). The Android twin writes the same documents
// (`WidgetSnapshot.java`).

/// The names the app reloads widgets by, and the extension declares them under.
public enum WidgetKind {
    public static let nowPlaying = "NowPlaying"
    public static let lastPlayed = "LastPlayed"
}

/// A cover inside the shared folder: `art/<sha256 of the cover URL>.jpg`. A file, never a URL: the
/// extension has no network and no cookie, so there is nothing it could do with one.
public struct ArtFile: Hashable {
    public let relativePath: String

    /// Nil unless `relativePath` has exactly the stored form, so a damaged document cannot name a
    /// file outside `art/`.
    public init?(relativePath: String) {
        let prefix = "art/", suffix = ".jpg"
        guard relativePath.hasPrefix(prefix), relativePath.hasSuffix(suffix) else { return nil }
        let name = relativePath.dropFirst(prefix.count).dropLast(suffix.count)
        guard name.count == 64, name.allSatisfy({ $0.isASCII && $0.isHexDigit }) else { return nil }
        self.relativePath = relativePath
    }

    /// `sha256Hex` is the hash of the cover's URL, so every track of an album shares one file.
    public init?(sha256Hex: String) {
        self.init(relativePath: "art/\(sha256Hex.lowercased()).jpg")
    }
}

public struct TrackInfo: Equatable {
    public let title: String
    public let artist: String
    public let album: String
    public let art: ArtFile?

    public init(title: String, artist: String, album: String, art: ArtFile?) {
        self.title = title
        self.artist = artist
        self.album = album
        self.art = art
    }
}

/// The player as it was at `asOf`. The position is not ticked into the file: a view derives the
/// present from `position`, `asOf` and `rate`.
public struct Transport: Equatable {
    public let playing: Bool
    public let position: Double
    public let duration: Double
    public let rate: Double
    public let asOf: Date
    public let canNext: Bool
    public let canPrevious: Bool

    public init(playing: Bool, position: Double, duration: Double, rate: Double, asOf: Date,
                canNext: Bool, canPrevious: Bool) {
        self.playing = playing
        self.position = position
        self.duration = duration
        self.rate = rate
        self.asOf = asOf
        self.canNext = canNext
        self.canPrevious = canPrevious
    }

    /// Where the song is at `date` if nothing has changed since `asOf`.
    public func position(at date: Date) -> Double {
        guard playing else { return position }
        let moved = position + date.timeIntervalSince(asOf) * rate
        return duration > 0 ? min(moved, duration) : moved
    }

    /// When the song ends if nothing changes. Nil when paused or the length is not known yet.
    public var expectedEnd: Date? {
        guard playing, duration > 0, rate > 0 else { return nil }
        return asOf.addingTimeInterval(max(0, duration - position) / rate)
    }

    /// The same player state a moment later: publishing it again would change nothing a widget
    /// shows, so it is not written.
    func agrees(with other: Transport) -> Bool {
        playing == other.playing && duration == other.duration && rate == other.rate
            && canNext == other.canNext && canPrevious == other.canPrevious
            && abs(position(at: other.asOf) - other.position) < 1.5
    }
}

/// Can a button act without the app in front?
///
/// `background` exists only with a `Transport` a live engine published, so a view cannot draw a
/// background button for a player nobody has seen. `needsApp` keeps the last track on screen and
/// turns the button into a link that opens the app.
public enum Reach: Equatable {
    case background(Transport)
    case needsApp
}

public enum NowPlaying: Equatable {
    case nothing
    case track(TrackInfo, Reach)

    /// What is left when no engine can be reached: the track stays, its transport goes.
    public var demoted: NowPlaying {
        guard case .track(let info, .background) = self else { return self }
        return .track(info, .needsApp)
    }

    func agrees(with other: NowPlaying) -> Bool {
        switch (self, other) {
        case (.track(let mine, .background(let left)), .track(let theirs, .background(let right))):
            return mine == theirs && left.agrees(with: right)
        default:
            return self == other
        }
    }
}

public struct ShelfItem: Equatable {
    /// The page's own stable key for the item (an album key). A widget link names it.
    public let id: String
    public let title: String
    public let subtitle: String
    public let art: ArtFile?
    /// The in-app path the page published for this item. Only the app reads it, when a link names
    /// `id`; a link never carries a path of its own.
    public let path: String

    public init?(id: String, title: String, subtitle: String, art: ArtFile?, path: String) {
        guard !id.isEmpty, Self.isAppPath(path) else { return nil }
        self.id = id
        self.title = title
        self.subtitle = subtitle
        self.art = art
        self.path = path
    }

    /// A path inside the app: one leading slash. Two would be read as another host.
    public static func isAppPath(_ path: String) -> Bool {
        path.hasPrefix("/") && !path.hasPrefix("//") && !path.contains("\\")
    }
}

public struct Shelf: Equatable {
    public static let capacity = 8
    public static let empty = Shelf(items: [])

    public let items: [ShelfItem]

    public init(items: [ShelfItem]) {
        self.items = Array(items.prefix(Self.capacity))
    }
}

// MARK: - On disk

private let wireVersion = 1

private struct NowPlayingWire: Codable {
    var v: Int
    var state: String
    var title: String?
    var artist: String?
    var album: String?
    var art: String?
    var reach: String?
    var transport: TransportWire?
}

private struct TransportWire: Codable {
    var playing: Bool
    var position: Double
    var duration: Double
    var rate: Double
    /// Milliseconds since 1970, as Android's `System.currentTimeMillis()` and the page's `Date.now()`.
    var asOf: Double
    var canNext: Bool
    var canPrevious: Bool
}

private struct ShelfWire: Codable {
    var v: Int
    var items: [ShelfItemWire]
}

private struct ShelfItemWire: Codable {
    var id: String
    var title: String
    var subtitle: String
    var art: String?
    var path: String
}

private func encoded<T: Encodable>(_ value: T) -> Data {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    // Only strings, numbers and booleans are encoded here, which cannot fail.
    return (try? encoder.encode(value)) ?? Data("{}".utf8)
}

extension NowPlaying {
    /// A document of another version, or one that does not parse, reads as `.nothing`: an
    /// extension from an older build must still draw something after an update changes the form.
    public init(json: Data) {
        guard let wire = try? JSONDecoder().decode(NowPlayingWire.self, from: json),
              wire.v == wireVersion, wire.state == "track" else {
            self = .nothing
            return
        }
        let info = TrackInfo(
            title: wire.title ?? "", artist: wire.artist ?? "", album: wire.album ?? "",
            art: wire.art.flatMap(ArtFile.init(relativePath:)))
        guard wire.reach == "background", let transport = wire.transport else {
            self = .track(info, .needsApp)
            return
        }
        self = .track(info, .background(Transport(
            playing: transport.playing, position: transport.position, duration: transport.duration,
            rate: transport.rate, asOf: Date(timeIntervalSince1970: transport.asOf / 1000),
            canNext: transport.canNext, canPrevious: transport.canPrevious)))
    }

    public var json: Data {
        guard case .track(let info, let reach) = self else {
            return encoded(NowPlayingWire(v: wireVersion, state: "nothing"))
        }
        var wire = NowPlayingWire(
            v: wireVersion, state: "track", title: info.title, artist: info.artist,
            album: info.album, art: info.art?.relativePath, reach: "needsApp")
        if case .background(let transport) = reach {
            wire.reach = "background"
            wire.transport = TransportWire(
                playing: transport.playing, position: transport.position,
                duration: transport.duration, rate: transport.rate,
                asOf: (transport.asOf.timeIntervalSince1970 * 1000).rounded(),
                canNext: transport.canNext, canPrevious: transport.canPrevious)
        }
        return encoded(wire)
    }
}

extension Shelf {
    public init(json: Data) {
        guard let wire = try? JSONDecoder().decode(ShelfWire.self, from: json),
              wire.v == wireVersion else {
            self = .empty
            return
        }
        self.init(items: wire.items.compactMap { item in
            ShelfItem(id: item.id, title: item.title, subtitle: item.subtitle,
                      art: item.art.flatMap(ArtFile.init(relativePath:)), path: item.path)
        })
    }

    public var json: Data {
        encoded(ShelfWire(v: wireVersion, items: items.map { item in
            ShelfItemWire(id: item.id, title: item.title, subtitle: item.subtitle,
                          art: item.art?.relativePath, path: item.path)
        }))
    }
}
