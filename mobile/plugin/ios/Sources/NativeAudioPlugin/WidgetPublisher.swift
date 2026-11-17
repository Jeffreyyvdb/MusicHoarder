import CryptoKit
import Foundation
import UIKit
import WidgetBridge
import WidgetKit

/// The app's writes to the folder its Home Screen widgets read (`WidgetStore`), and the reload
/// that follows each one. The audio engine publishes what is playing, the `Widgets` plugin the
/// shelf. A build without an App Group has no store, and everything here does nothing.
/// Main thread only.
final class WidgetPublisher {
    static let shared = WidgetPublisher()

    /// Longest side of a stored cover, in pixels: enough for a medium widget's cover at 3x, and
    /// small enough to stay under WidgetKit's limit on an archived image.
    private static let artPixels: CGFloat = 512

    private let store: WidgetStore?
    /// Covers being encoded now, with whoever waits for each.
    private var storing: [ArtFile: [(Bool) -> Void]] = [:]
    /// Covers stored for a shelf that is not written yet: pruning must leave them.
    private var shelfArt = Set<ArtFile>()

    init(store: WidgetStore? = WidgetStore.forThisBundle()) {
        self.store = store
    }

    var hasSharedContainer: Bool { store != nil }

    /// The file a cover is kept under: the hash of its URL, so every track of an album shares
    /// one, and a file name says nothing about the server it came from.
    static func artFile(for cover: URL) -> ArtFile? {
        let digest = SHA256.hash(data: Data(cover.absoluteString.utf8))
        return ArtFile(sha256Hex: digest.map { String(format: "%02x", $0) }.joined())
    }

    func hasArt(_ art: ArtFile) -> Bool {
        store?.hasArt(art) ?? false
    }

    /// Cheap to call often: a snapshot that says what is already stored writes nothing and
    /// reloads nothing.
    func publish(_ nowPlaying: NowPlaying) {
        guard let store, store.write(nowPlaying) else { return }
        store.pruneArt(keeping: shelfArt)
        WidgetCenter.shared.reloadTimelines(ofKind: WidgetKind.nowPlaying)
    }

    func publish(_ shelf: Shelf) {
        shelfArt = []
        guard let store, store.write(shelf) else { return }
        store.pruneArt()
        WidgetCenter.shared.reloadTimelines(ofKind: WidgetKind.lastPlayed)
    }

    /// The covers a shelf still being put together will name.
    func protect(_ art: Set<ArtFile>) {
        shelfArt = art
    }

    /// No engine can be reached any more: the track stays on the widget, and its buttons open
    /// the app.
    func demote() {
        guard let store, store.demoteNowPlaying() else { return }
        WidgetCenter.shared.reloadTimelines(ofKind: WidgetKind.nowPlaying)
    }

    /// Sign-out, another account or another server: nothing of the old one stays.
    func clear() {
        shelfArt = []
        guard let store else { return }
        store.clear()
        WidgetCenter.shared.reloadAllTimelines()
    }

    /// Keep `image` as `art`, scaled down. `done` hears whether the file is there afterwards.
    func keep(_ image: UIImage, as art: ArtFile, then done: @escaping (Bool) -> Void) {
        guard let store else { return done(false) }
        if store.hasArt(art) { return done(true) }
        if storing[art] != nil {
            storing[art]?.append(done)
            return
        }
        storing[art] = [done]
        DispatchQueue.global(qos: .utility).async {
            let stored = Self.jpeg(image).map { store.storeArt($0, as: art) } ?? false
            DispatchQueue.main.async {
                let waiting = self.storing.removeValue(forKey: art) ?? []
                waiting.forEach { $0(stored) }
            }
        }
    }

    private static func jpeg(_ image: UIImage) -> Data? {
        let pixels = CGSize(width: image.size.width * image.scale,
                            height: image.size.height * image.scale)
        let longest = max(pixels.width, pixels.height)
        guard longest > 0 else { return nil }
        let shrink = min(1, artPixels / longest)
        let size = CGSize(width: max(1, (pixels.width * shrink).rounded()),
                          height: max(1, (pixels.height * shrink).rounded()))
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        format.opaque = true
        return UIGraphicsImageRenderer(size: size, format: format)
            .jpegData(withCompressionQuality: 0.85) { _ in
                image.draw(in: CGRect(origin: .zero, size: size))
            }
    }
}
