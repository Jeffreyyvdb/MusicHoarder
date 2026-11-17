import Capacitor
import Foundation
import UIKit
import WebKit
import WidgetBridge

/// The page's side of the Home Screen widgets: it hands over the "Last played" shelf, and takes
/// what a tap on a widget asked for. A plugin of its own, so a page can tell a build with widgets
/// from one without by whether `Widgets` exists. Now Playing needs none of this: the audio engine
/// feeds it from what every page already sends.
///
/// The web side is `frontend/src/lib/native/widgets.ts` and the Android twin `WidgetsPlugin.java`.
/// One protocol: add methods and fields, never rename or remove them.
@objc(WidgetsPlugin)
public class WidgetsPlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "WidgetsPlugin"
    public let jsName = "Widgets"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "setShelf", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "clear", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "takeLaunchAction", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "status", returnType: CAPPluginReturnPromise)
    ]

    private struct ShelfEntry {
        let id: String
        let title: String
        let subtitle: String
        let path: String
        let cover: (url: URL, file: ArtFile)?
    }

    /// Raised by every `setShelf` and `clear`. A shelf whose covers finish downloading after a
    /// newer one was asked for is not written.
    private var generation = 0
    private var serverObserver: NSObjectProtocol?

    override public func load() {
        DispatchQueue.main.async {
            LaunchMailbox.shared.onPut = { [weak self] in
                self?.notifyListeners("launchAction", data: [:])
            }
        }
        // Another server was chosen: what the widgets show belongs to the one before it.
        serverObserver = NotificationCenter.default.addObserver(
            forName: ShellServerPlugin.changedNotification, object: nil, queue: .main
        ) { [weak self] _ in
            self?.generation += 1
            WidgetPublisher.shared.clear()
        }
    }

    deinit {
        if let serverObserver { NotificationCenter.default.removeObserver(serverObserver) }
    }

    // MARK: - Methods

    /// `{ items: [{ id, title, subtitle, artwork: absoluteUrl | null, path }] }`, the Overview's
    /// "Last played" as the page worked it out. Resolves once taken; the covers follow.
    @objc func setShelf(_ call: CAPPluginCall) {
        guard let items = call.getArray("items") else {
            call.reject("A shelf needs a list of items.")
            return
        }
        let entries = Array(items.compactMap { Self.entry($0) }.prefix(Shelf.capacity))
        call.resolve()
        DispatchQueue.main.async { self.download(entries) }
    }

    /// Sign-out or another account: both documents and every cover go.
    @objc func clear(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            self.generation += 1
            WidgetPublisher.shared.clear()
            call.resolve()
        }
    }

    /// `{ action: null | { kind: "now-playing" } | { kind: "resume" } | { kind: "open", path } }`.
    /// Whatever a tap on a widget asked for, once: the same action is never handed over twice.
    @objc func takeLaunchAction(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            let action: Any
            switch LaunchMailbox.shared.take() {
            case .nowPlaying?:
                action = ["kind": "now-playing"]
            case .resume?:
                action = ["kind": "resume"]
            case .open(let path)?:
                action = ["kind": "open", "path": path]
            case nil:
                action = NSNull()
            }
            call.resolve(["action": action])
        }
    }

    /// `{ sharedContainer }`: false when this build has no App Group, so its widgets can show
    /// nothing. Asked from Web Inspector when a widget stays blank (mobile/README.md).
    @objc func status(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            call.resolve(["sharedContainer": WidgetPublisher.shared.hasSharedContainer])
        }
    }

    // MARK: - The shelf

    private static func entry(_ value: Any) -> ShelfEntry? {
        guard let object = value as? JSObject,
              let id = object["id"] as? String, !id.isEmpty,
              let path = object["path"] as? String, ShelfItem.isAppPath(path)
        else { return nil }
        var cover: (url: URL, file: ArtFile)?
        if let raw = object["artwork"] as? String, let url = URL(string: raw),
           let scheme = url.scheme?.lowercased(), scheme == "https" || scheme == "http",
           let file = WidgetPublisher.artFile(for: url) {
            cover = (url, file)
        }
        return ShelfEntry(
            id: id,
            title: object["title"] as? String ?? "",
            subtitle: object["subtitle"] as? String ?? "",
            path: path,
            cover: cover)
    }

    /// Fetch the covers the store does not hold yet, then write the shelf. A cover that could
    /// not be fetched leaves its item without one.
    private func download(_ entries: [ShelfEntry]) {
        generation += 1
        let asked = generation
        let publisher = WidgetPublisher.shared
        guard publisher.hasSharedContainer else { return }

        var covers: [ArtFile: URL] = [:]
        for entry in entries {
            if let cover = entry.cover { covers[cover.file] = cover.url }
        }
        publisher.protect(Set(covers.keys))
        let missing = covers.filter { !publisher.hasArt($0.key) }

        let write: () -> Void = { [weak self] in
            guard let self, self.generation == asked else { return }
            publisher.publish(Shelf(items: entries.compactMap { entry in
                ShelfItem(
                    id: entry.id, title: entry.title, subtitle: entry.subtitle,
                    art: entry.cover.flatMap { publisher.hasArt($0.file) ? $0.file : nil },
                    path: entry.path)
            }))
        }
        guard !missing.isEmpty,
              let cookieStore = bridge?.webView?.configuration.websiteDataStore.httpCookieStore
        else { return write() }

        cookieStore.getAllCookies { cookies in
            let fetched = DispatchGroup()
            for (file, url) in missing {
                fetched.enter()
                // The session cookie authorises a cover as it does a stream, and goes to the
                // host it belongs to and no other.
                var request = URLRequest(url: url)
                request.httpShouldHandleCookies = false
                let own = AudioEngine.cookies(cookies, for: url)
                for (name, value) in HTTPCookie.requestHeaderFields(with: own) {
                    request.setValue(value, forHTTPHeaderField: name)
                }
                URLSession.shared.dataTask(with: request) { data, response, _ in
                    let ok = ((response as? HTTPURLResponse)?.statusCode).map { (200..<300).contains($0) } ?? false
                    guard ok, let data, let image = UIImage(data: data) else { return fetched.leave() }
                    DispatchQueue.main.async {
                        publisher.keep(image, as: file) { _ in fetched.leave() }
                    }
                }.resume()
            }
            fetched.notify(queue: .main, execute: write)
        }
    }
}
