import AppIntents
import WidgetBridge
import WidgetKit

/// What a button on the Now Playing widget runs.
///
/// A source file of BOTH targets, App and MusicHoarderWidgets. The widget needs the type to draw
/// `Button(intent:)`. The app needs it because the system runs `perform()` of an
/// `AudioPlaybackIntent` in the app's process, starting the app in the background when it has
/// to, and that is what lets a press start audio without the app coming to the front. It is not
/// in the Swift package: App Intents are found per target, and one that only exists in a static
/// library may not be.
@available(iOS 17.0, *)
struct PlaybackCommandIntent: AudioPlaybackIntent {
    static let title: LocalizedStringResource = "Control playback"
    /// A widget button only: not offered in Shortcuts, Spotlight or Siri.
    static let isDiscoverable = false

    @Parameter(title: "Command")
    var command: String

    init() {}

    init(_ command: WidgetCommand) {
        self.command = command.rawValue
    }

    #if compiler(>=6.2)
    /// iOS 26: stay in the background, and allow `perform()` to ask for the foreground when no
    /// player is left to act there. If a press that should stay in the background ever brings
    /// the app forward, this and the block in `perform()` are what to remove.
    @available(iOS 26.0, *)
    static var supportedModes: IntentModes { [.background, .foreground(.dynamic)] }
    #endif

    @MainActor
    func perform() async throws -> some IntentResult {
        guard let command = WidgetCommand(rawValue: command) else { return .result() }
        // Returns once the page or the device has acted, since iOS may suspend the app the
        // moment this function does. When neither could, the stored snapshot has been demoted
        // and the widget's button is now a link that opens the app.
        let outcome: CommandOutcome = await withCheckedContinuation { continuation in
            WidgetCommandRouter.shared.send(command) { continuation.resume(returning: $0) }
        }
        #if compiler(>=6.2)
        if outcome == .needsApp, command == .play, #available(iOS 26.0, *),
           systemContext.currentMode.canContinueInForeground {
            // The process that held the player is gone and the widget had not heard. Open the
            // app in this same press, and leave the page the request it would get from the link.
            do {
                try await continueInForeground(nil, alwaysConfirm: false)
                LaunchMailbox.shared.put(.resume)
            } catch {
                // Not allowed after all: the widget's button is a link by now, as on iOS 17.
            }
        }
        #endif
        WidgetCenter.shared.reloadTimelines(ofKind: WidgetKind.nowPlaying)
        return .result()
    }
}
