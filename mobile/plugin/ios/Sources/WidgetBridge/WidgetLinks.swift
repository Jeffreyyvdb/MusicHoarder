import Foundation

/// The closed set of things a tap on a widget can ask for. A link never carries a server address
/// or a path: anything on the phone can open a `musichoarder://` link, so the most one can do is
/// resume the user's own music or open an album their own page put on the shelf.
public enum WidgetLink: Equatable {
    case nowPlaying
    case resume
    case shelfItem(id: String)

    private static let unreserved = CharacterSet(
        charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")

    /// `musichoarder://widget/now-playing`, `/resume`, `/shelf/<id>`.
    public var url: URL {
        var parts = URLComponents()
        parts.scheme = "musichoarder"
        parts.host = "widget"
        switch self {
        case .nowPlaying:
            parts.path = "/now-playing"
        case .resume:
            parts.path = "/resume"
        case .shelfItem(let id):
            // An id is the page's own key and may hold anything, a slash included. ASCII only
            // stays as it is: `.alphanumerics` would let other scripts' letters through, and
            // `percentEncodedPath` traps on those.
            let encoded = id.addingPercentEncoding(withAllowedCharacters: Self.unreserved) ?? ""
            parts.percentEncodedPath = "/shelf/" + encoded
        }
        // Every part above is valid by construction.
        return parts.url ?? URL(fileURLWithPath: "/")
    }

    /// Nil for anything else, a well-formed `musichoarder://auth` link included.
    public init?(_ url: URL) {
        guard let parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
              parts.scheme?.lowercased() == "musichoarder", parts.host?.lowercased() == "widget",
              parts.query == nil, parts.fragment == nil
        else { return nil }
        let segments = parts.percentEncodedPath
            .split(separator: "/", omittingEmptySubsequences: false).dropFirst().map(String.init)
        switch segments {
        case ["now-playing"]:
            self = .nowPlaying
        case ["resume"]:
            self = .resume
        default:
            guard segments.count == 2, segments[0] == "shelf",
                  let id = segments[1].removingPercentEncoding, !id.isEmpty
            else { return nil }
            self = .shelfItem(id: id)
        }
    }

    /// What the page is asked to do. A shelf id resolves to the path the page itself published
    /// for it; an id the current shelf does not hold asks for nothing (the app just opens).
    public func action(in shelf: Shelf) -> LaunchAction? {
        switch self {
        case .nowPlaying:
            return .nowPlaying
        case .resume:
            return .resume
        case .shelfItem(let id):
            return shelf.items.first { $0.id == id }.map { .open(path: $0.path) }
        }
    }
}

public enum LaunchAction: Equatable {
    case nowPlaying
    case resume
    case open(path: String)
}

/// One pending action, held in memory in the app process until the page takes it. The page asks
/// when it starts and whenever it becomes visible, so a warm and a cold start work the same, and
/// native code never loads a URL for a tap. Main thread only.
public final class LaunchMailbox {
    public static let shared = LaunchMailbox()
    /// An action nobody took in this long is dropped: a slow start must not begin playing music
    /// long after the tap, and a page from before the widgets never takes one.
    public static let lifetime: TimeInterval = 30

    /// Called after `put`, so the plugin can nudge a page that is already running.
    public var onPut: (() -> Void)?

    private var pending: (action: LaunchAction, at: Date)?

    public init() {}

    /// The latest action wins.
    public func put(_ action: LaunchAction, now: Date = Date()) {
        pending = (action, now)
        onPut?()
    }

    /// The pending action, once: a second call returns nil.
    public func take(now: Date = Date()) -> LaunchAction? {
        defer { pending = nil }
        guard let pending, now.timeIntervalSince(pending.at) <= Self.lifetime else { return nil }
        return pending.action
    }
}
