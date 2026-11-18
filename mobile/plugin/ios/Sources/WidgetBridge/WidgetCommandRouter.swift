import Foundation

/// What a widget button asks for. Never "toggle": a button carries what its glyph showed when it
/// was pressed, so a press on a stale widget cannot do the opposite of what it promised.
public enum WidgetCommand: String, CaseIterable {
    case play, pause, next, previous
}

public enum CommandOutcome: Equatable {
    /// The page took the command, or the device carried it out.
    case carriedOut
    /// Nobody could: no player in this process, nothing loaded, or the audio would not start.
    /// The stored snapshot has been demoted, so the widget now offers to open the app.
    case needsApp
}

/// Where a widget button's intent, which the app target owns, meets the audio engine, which the
/// plugin owns: neither can see the other's types. Main thread only.
public final class WidgetCommandRouter {
    public static let shared = WidgetCommandRouter()

    /// Carries a command out and reports whether it took effect.
    public typealias Player = (_ command: WidgetCommand, _ done: @escaping (Bool) -> Void) -> Void

    /// Set by the audio plugin while it has an engine. Nil in a process the system started only
    /// to run an intent: no web view was built there, so there is no player.
    public var player: Player?

    private let store: WidgetStore?

    public init(store: WidgetStore? = WidgetStore.forThisBundle()) {
        self.store = store
    }

    /// `completion` runs once the command has had its effect, or could not. Not before: iOS may
    /// suspend the app as soon as the intent that called this returns.
    public func send(_ command: WidgetCommand, completion: @escaping (CommandOutcome) -> Void) {
        guard let player else {
            store?.demoteNowPlaying()
            completion(.needsApp)
            return
        }
        player(command) { [store] carriedOut in
            if !carriedOut { store?.demoteNowPlaying() }
            completion(carriedOut ? .carriedOut : .needsApp)
        }
    }
}
