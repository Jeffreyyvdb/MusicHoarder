import Capacitor
import Foundation

/// Which MusicHoarder server the app loads. The app ships without one (a build may still name a
/// default, `MH_SERVER_URL`), so the bundled page asks for an address and hands it here. The app
/// itself reads the choice when it builds its web view (`SceneDelegate.swift`), under the same
/// key: change both together.
///
/// The web side is `mobile/www/index.html` and `frontend/src/lib/native/shell.ts`.
@objc(ShellServerPlugin)
public class ShellServerPlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "ShellServerPlugin"
    public let jsName = "ShellServer"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "getServer", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "setServer", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "clearServer", returnType: CAPPluginReturnPromise)
    ]

    /// UserDefaults key of the chosen server's origin. Read by the app's SceneDelegate.
    static let storageKey = "MusicHoarderServerURL"
    /// Posted (after the call resolves) to make the app build its web view again for the choice.
    static let changedNotification = Notification.Name("MusicHoarderServerChanged")

    /// The server the app has loaded, or null when it is showing the bundled page with none.
    @objc func getServer(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            let loaded = self.bridge?.config.serverURL
            let remote = loaded.flatMap { Self.origin(of: $0) }
            call.resolve([
                "url": remote?.absoluteString ?? NSNull(),
                "chosen": UserDefaults.standard.string(forKey: Self.storageKey) != nil
            ])
        }
    }

    /// Take `url` as the server, if it is a MusicHoarder server reachable over https, and load it.
    /// Rejected with a code the page words: `invalid`, `unreachable` or `not_musichoarder`.
    @objc func setServer(_ call: CAPPluginCall) {
        guard let raw = call.getString("url"), let origin = Self.parse(raw) else {
            call.reject("That is not a web address.", "invalid")
            return
        }
        Self.verify(origin) { result in
            DispatchQueue.main.async {
                switch result {
                case .success:
                    UserDefaults.standard.set(origin.absoluteString, forKey: Self.storageKey)
                    call.resolve(["url": origin.absoluteString])
                    Self.announceChange()
                case .failure(let error):
                    call.reject(error.message, error.code)
                }
            }
        }
    }

    /// Forget the choice: the app loads the build's own server again, or asks for one.
    @objc func clearServer(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            UserDefaults.standard.removeObject(forKey: Self.storageKey)
            call.resolve()
            Self.announceChange()
        }
    }

    // MARK: - Helpers

    /// After the reply has had a moment to reach the page that asked.
    private static func announceChange() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) {
            NotificationCenter.default.post(name: changedNotification, object: nil)
        }
    }

    /// "music.example.com", "https://music.example.com/overview" → "https://music.example.com".
    /// Plain http is refused, as iOS refuses it anyway.
    static func parse(_ raw: String) -> URL? {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty { return nil }
        if !text.contains("://") { text = "https://" + text }
        guard let url = URL(string: text), url.scheme?.lowercased() == "https" else { return nil }
        return origin(of: url)
    }

    static func origin(of url: URL) -> URL? {
        guard let scheme = url.scheme?.lowercased(), scheme == "https" || scheme == "http",
              let host = url.host?.lowercased(), !host.isEmpty else { return nil }
        var parts = URLComponents()
        parts.scheme = scheme
        parts.host = host
        parts.port = url.port
        return parts.url
    }

    private struct Failure: Error {
        let code: String
        let message: String
    }

    /// The server's web app manifest names MusicHoarder: anything else (a typo that happens to
    /// load, a captive portal) would leave the app on a page with no way back to this one.
    private static func verify(_ origin: URL, completion: @escaping (Result<Void, Failure>) -> Void) {
        var request = URLRequest(url: origin.appendingPathComponent("manifest.webmanifest"))
        request.timeoutInterval = 15
        request.cachePolicy = .reloadIgnoringLocalCacheData
        URLSession.shared.dataTask(with: request) { data, response, error in
            if error != nil {
                completion(.failure(Failure(code: "unreachable", message: "Could not reach that address.")))
                return
            }
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            let finalOrigin = response?.url.flatMap { Self.origin(of: $0) }
            guard (200..<300).contains(status), finalOrigin == origin, let data,
                  let manifest = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  (manifest["name"] as? String) == "MusicHoarder"
            else {
                completion(.failure(Failure(
                    code: "not_musichoarder", message: "That address is not a MusicHoarder server.")))
                return
            }
            completion(.success(()))
        }.resume()
    }
}
