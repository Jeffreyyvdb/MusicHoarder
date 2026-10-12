import UIKit
import Capacitor
import WebKit

class SceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?
    private var bridgeController: CAPBridgeViewController?
    private var themeObservation: NSKeyValueObservation?

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options connectionOptions: UIScene.ConnectionOptions) {
        guard let windowScene = scene as? UIWindowScene else { return }

        let controller = CAPBridgeViewController()
        bridgeController = controller
        window = UIWindow(windowScene: windowScene)
        window?.rootViewController = controller
        window?.makeKeyAndVisible()
        controller.loadViewIfNeeded()
        followThemeColor()

        SceneDelegateProxy.shared.scene(scene, willConnectTo: session, options: connectionOptions)
        connectionOptions.urlContexts.forEach { open($0.url) }
    }

    func scene(_ scene: UIScene, openURLContexts URLContexts: Set<UIOpenURLContext>) {
        URLContexts.forEach { open($0.url) }
        SceneDelegateProxy.shared.scene(scene, openURLContexts: URLContexts)
    }

    func scene(_ scene: UIScene, continue userActivity: NSUserActivity) {
        SceneDelegateProxy.shared.scene(scene, continue: userActivity)
    }

    /// `musichoarder://auth?token=…&url=…`, the link on the page an emailed sign-in link opens in
    /// Safari when it was asked for from this app (the frontend's `/auth/callback`). The web view
    /// has its own cookies, so the token is spent here: the same callback, loaded in the web view,
    /// sets the session cookie where the app can use it.
    private func open(_ url: URL) {
        guard url.scheme?.lowercased() == "musichoarder", url.host?.lowercased() == "auth",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems,
              let token = items.first(where: { $0.name == "token" })?.value, !token.isEmpty,
              let server = bridgeController?.bridge?.config.serverURL,
              let scheme = server.scheme?.lowercased(), scheme == "https" || scheme == "http",
              var callback = URLComponents(url: server, resolvingAgainstBaseURL: false)
        else { return }

        // The link names the server it was made for. The app only ever loads its own, so a link
        // made for another one (or made up) goes nowhere.
        if let named = items.first(where: { $0.name == "url" })?.value, !named.isEmpty {
            guard let other = URL(string: named), Self.sameOrigin(other, server) else { return }
        }

        callback.path = "/auth/callback"
        callback.queryItems = [URLQueryItem(name: "token", value: token)]
        callback.fragment = nil
        guard let target = callback.url else { return }
        bridgeController?.webView?.load(URLRequest(url: target))
    }

    private static func sameOrigin(_ left: URL, _ right: URL) -> Bool {
        left.scheme?.lowercased() == right.scheme?.lowercased()
            && left.host?.lowercased() == right.host?.lowercased()
            && left.port == right.port
    }

    /// The status bar's text follows the page under it: white over a dark page, black over a light
    /// one. The pages keep `<meta name="theme-color">` on the surface they paint (the root layout
    /// does, for Safari's tab bar and the installed web app), which is not always the system's
    /// appearance: the app has its own theme setting, and Now Playing is dark in either.
    private func followThemeColor() {
        guard let webView = bridgeController?.webView else { return }
        themeObservation = webView.observe(\.themeColor, options: [.initial]) { [weak self] webView, _ in
            DispatchQueue.main.async {
                self?.applyStatusBarStyle(for: webView.themeColor)
            }
        }
    }

    private func applyStatusBarStyle(for color: UIColor?) {
        guard let bridge = bridgeController?.bridge else { return }
        var white: CGFloat = 1
        guard let color, color.getWhite(&white, alpha: nil) else {
            bridge.statusBarStyle = .default
            return
        }
        bridge.statusBarStyle = white < 0.5 ? .lightContent : .darkContent
    }
}
