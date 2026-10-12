import AVKit
import Capacitor
import Foundation
import UIKit
import WebKit

/// The bridge between the web player and `AudioEngine`. The web side is
/// `frontend/src/lib/native/native-audio.ts`; the two are one protocol and change together.
@objc(NativeAudioPlugin)
public class NativeAudioPlugin: CAPPlugin, CAPBridgedPlugin, AudioEngineDelegate {
    public let identifier = "NativeAudioPlugin"
    public let jsName = "NativeAudio"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "loadTrack", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "setUpcoming", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "setMetadata", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "play", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "pause", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "seek", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "setRate", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "setVolume", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "stop", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "setCommands", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "ackRemote", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "getState", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "showRoutePicker", returnType: CAPPluginReturnPromise)
    ]

    private var engine: AudioEngine?
    private var routePicker: AVRoutePickerView?

    override public func load() {
        DispatchQueue.main.async {
            let engine = AudioEngine()
            engine.delegate = self
            self.engine = engine
        }
    }

    // MARK: - AudioEngineDelegate

    func audioEngine(_ engine: AudioEngine, didEmit event: String, data: [String: Any]) {
        notifyListeners(event, data: data)
    }

    func audioEngineCookies(_ engine: AudioEngine, completion: @escaping ([HTTPCookie]) -> Void) {
        guard let store = bridge?.webView?.configuration.websiteDataStore.httpCookieStore else {
            completion([])
            return
        }
        store.getAllCookies(completion)
    }

    // MARK: - Methods

    @objc func loadTrack(_ call: CAPPluginCall) {
        guard let track = Self.track({ call.options[$0] }) else {
            call.reject("A track needs an http(s) url.")
            return
        }
        let position = call.getDouble("position") ?? 0
        let autoplay = call.getBool("autoplay") ?? false
        let loadId = call.getInt("loadId") ?? 0
        let seq = call.getInt("seq") ?? 0
        onMain(call) { engine in
            engine.load(track, position: position, autoplay: autoplay, loadId: loadId, seq: seq)
        }
    }

    @objc func setUpcoming(_ call: CAPPluginCall) {
        let items = call.getArray("items") ?? []
        let tracks = items.compactMap { item -> Track? in
            guard let object = item as? JSObject else { return nil }
            return Self.track { object[$0] }
        }
        let loadId = call.getInt("loadId") ?? 0
        onMain(call) { $0.setUpcoming(tracks, loadId: loadId) }
    }

    @objc func setMetadata(_ call: CAPPluginCall) {
        guard let track = Self.track({ call.options[$0] }) else {
            call.reject("A track needs an http(s) url.")
            return
        }
        let loadId = call.getInt("loadId") ?? 0
        onMain(call) { $0.setMetadata(track, loadId: loadId) }
    }

    @objc func play(_ call: CAPPluginCall) {
        let seq = call.getInt("seq") ?? 0
        onMain(call) { $0.play(seq: seq) }
    }

    @objc func pause(_ call: CAPPluginCall) {
        let seq = call.getInt("seq") ?? 0
        onMain(call) { $0.pause(seq: seq) }
    }

    @objc func seek(_ call: CAPPluginCall) {
        let time = call.getDouble("time") ?? 0
        let seq = call.getInt("seq") ?? 0
        onMain(call) { $0.seek(to: time, seq: seq) }
    }

    @objc func setRate(_ call: CAPPluginCall) {
        let rate = Float(call.getDouble("rate") ?? 1)
        onMain(call) { $0.setRate(rate) }
    }

    @objc func setVolume(_ call: CAPPluginCall) {
        let volume = Float(call.getDouble("volume") ?? 1)
        onMain(call) { $0.setVolume(volume) }
    }

    @objc func stop(_ call: CAPPluginCall) {
        onMain(call) { $0.stop() }
    }

    @objc func setCommands(_ call: CAPPluginCall) {
        let next = call.getBool("next") ?? false
        let previous = call.getBool("previous") ?? false
        onMain(call) { $0.setCommands(next: next, previous: previous) }
    }

    @objc func ackRemote(_ call: CAPPluginCall) {
        let id = call.getInt("id") ?? 0
        onMain(call) { $0.acknowledgeRemote(id) }
    }

    @objc func getState(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            call.resolve(self.engine?.state() ?? [:])
        }
    }

    /// Open the system's AirPlay and Bluetooth route sheet. There is no API for it, only the
    /// button of a route picker view, so one is kept out of sight and pressed.
    @objc func showRoutePicker(_ call: CAPPluginCall) {
        DispatchQueue.main.async {
            guard let host = self.bridge?.viewController?.view else {
                call.reject("No view to present from.")
                return
            }
            let picker = self.routePicker ?? AVRoutePickerView(frame: CGRect(x: 0, y: 0, width: 1, height: 1))
            picker.isHidden = true
            if picker.superview == nil { host.addSubview(picker) }
            self.routePicker = picker
            for case let button as UIButton in picker.subviews {
                button.sendActions(for: .touchUpInside)
            }
            call.resolve()
        }
    }

    // MARK: - Helpers

    private func onMain(_ call: CAPPluginCall, _ body: @escaping (AudioEngine) -> Void) {
        DispatchQueue.main.async {
            guard let engine = self.engine else {
                call.reject("The player is not ready.")
                return
            }
            body(engine)
            call.resolve()
        }
    }

    private static func track(_ value: (String) -> Any?) -> Track? {
        guard let raw = value("url") as? String,
              let url = URL(string: raw),
              let scheme = url.scheme?.lowercased(), scheme == "https" || scheme == "http"
        else { return nil }
        let artwork = (value("artwork") as? String).flatMap { URL(string: $0) }
        return Track(
            url: url,
            title: value("title") as? String ?? "",
            artist: value("artist") as? String ?? "",
            album: value("album") as? String ?? "",
            artworkUrl: artwork
        )
    }
}
