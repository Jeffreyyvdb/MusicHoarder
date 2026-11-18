import Foundation

/// The folder the app and its widgets share. The only type that knows a path or a file name.
///
///     <App Group container>/widgets/now-playing.json   one writer: the audio engine
///     <App Group container>/widgets/shelf.json         one writer: the Widgets plugin
///     <App Group container>/widgets/art/<sha256>.jpg   written before the document that names it
///
/// The app process writes and the widget extension only reads. Every write goes to a temporary
/// file that is renamed into place, so the other process sees the old document or the new one.
/// No cookie, token or server address is ever stored here.
public struct WidgetStore {
    public let root: URL

    public init(root: URL) {
        self.root = root
    }

    /// The store of the running bundle, or nil when this build has no App Group (a signed build
    /// whose entitlement went missing, say). The group's id is the bundle's `MHAppGroup`
    /// Info.plist value, filled from the `MH_APP_GROUP` build setting in the app and in the
    /// extension, so a build with its own bundle id needs no code change.
    public static func forThisBundle(_ bundle: Bundle = .main) -> WidgetStore? {
        guard let group = bundle.object(forInfoDictionaryKey: "MHAppGroup") as? String,
              !group.isEmpty, !group.contains("$("),
              let container = FileManager.default.containerURL(
                forSecurityApplicationGroupIdentifier: group)
        else { return nil }
        return WidgetStore(root: container.appendingPathComponent("widgets", isDirectory: true))
    }

    private var nowPlayingFile: URL { root.appendingPathComponent("now-playing.json") }
    private var shelfFile: URL { root.appendingPathComponent("shelf.json") }
    private var artFolder: URL { root.appendingPathComponent("art", isDirectory: true) }

    // MARK: - Reading

    public func readNowPlaying() -> NowPlaying {
        (try? Data(contentsOf: nowPlayingFile)).map(NowPlaying.init(json:)) ?? .nothing
    }

    public func readShelf() -> Shelf {
        (try? Data(contentsOf: shelfFile)).map(Shelf.init(json:)) ?? .empty
    }

    /// Where a cover is kept, whether or not the file is there yet.
    public func url(of art: ArtFile) -> URL {
        root.appendingPathComponent(art.relativePath)
    }

    public func hasArt(_ art: ArtFile) -> Bool {
        FileManager.default.fileExists(atPath: url(of: art).path)
    }

    // MARK: - Writing (the app process only)

    /// False when what is stored already says the same, and nothing was written.
    @discardableResult
    public func write(_ nowPlaying: NowPlaying) -> Bool {
        if readNowPlaying().agrees(with: nowPlaying) { return false }
        return replace(nowPlayingFile, with: nowPlaying.json)
    }

    @discardableResult
    public func write(_ shelf: Shelf) -> Bool {
        if readShelf() == shelf { return false }
        return replace(shelfFile, with: shelf.json)
    }

    /// False when the file could not be written.
    @discardableResult
    public func storeArt(_ jpeg: Data, as art: ArtFile) -> Bool {
        replace(url(of: art), with: jpeg)
    }

    /// No engine can be reached any more: see `NowPlaying.demoted`. True when that changed what
    /// is stored.
    @discardableResult
    public func demoteNowPlaying() -> Bool {
        let stored = readNowPlaying()
        guard stored.demoted != stored else { return false }
        return replace(nowPlayingFile, with: stored.demoted.json)
    }

    /// Delete the covers neither document names, except those in `keeping` (covers stored for a
    /// document that is about to be written).
    public func pruneArt(keeping: Set<ArtFile> = []) {
        var named = keeping
        if case .track(let info, _) = readNowPlaying(), let art = info.art { named.insert(art) }
        readShelf().items.compactMap(\.art).forEach { named.insert($0) }
        let kept = Set(named.map { url(of: $0).lastPathComponent })
        let files = (try? FileManager.default.contentsOfDirectory(atPath: artFolder.path)) ?? []
        for file in files where !kept.contains(file) {
            try? FileManager.default.removeItem(at: artFolder.appendingPathComponent(file))
        }
    }

    /// Nothing of the account that was signed in stays: both documents and every cover.
    public func clear() {
        try? FileManager.default.removeItem(at: root)
    }

    private func replace(_ file: URL, with data: Data) -> Bool {
        do {
            try FileManager.default.createDirectory(
                at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try data.write(to: file, options: .atomic)
            return true
        } catch {
            return false
        }
    }
}
