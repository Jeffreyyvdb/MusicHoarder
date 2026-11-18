import SwiftUI
import WidgetKit

/// The Home Screen widgets. This extension reads what the app left in their shared folder and
/// draws it: it makes no network request and writes nothing. A button runs in the app's process
/// (`Shared/PlaybackCommandIntent.swift`), and a tap anywhere else opens the app with a link.
@main
struct MusicHoarderWidgetBundle: WidgetBundle {
    var body: some Widget {
        NowPlayingWidget()
        LastPlayedWidget()
    }
}
