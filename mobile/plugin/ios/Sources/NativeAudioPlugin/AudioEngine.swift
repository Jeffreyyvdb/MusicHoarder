import AVFoundation
import Foundation
import MediaPlayer
import UIKit

/// One song as the web player describes it.
struct Track {
    let url: URL
    let title: String
    let artist: String
    let album: String
    let artworkUrl: URL?
}

/// What the engine tells the web player. Every call arrives on the main thread.
protocol AudioEngineDelegate: AnyObject {
    func audioEngine(_ engine: AudioEngine, didEmit event: String, data: [String: Any])
    /// The web view's cookies: the session cookie is what authorises a stream.
    func audioEngineCookies(_ engine: AudioEngine, completion: @escaping ([HTTPCookie]) -> Void)
}

/// The player behind the web app's transport.
///
/// The web player stays the one that decides what plays: it loads a track, and hands over the
/// tracks that follow it. What this adds is that a song's end no longer needs the page. The queue
/// player moves to the next item by itself, with the page asleep or not, and reports where it got
/// to (`trail`), so the page catches up whenever it next runs. Main thread only.
final class AudioEngine: NSObject {
    private final class Entry {
        var track: Track
        let item: AVPlayerItem
        var failed = false
        var statusObservation: NSKeyValueObservation?

        init(track: Track, item: AVPlayerItem) {
            self.track = track
            self.item = item
        }
    }

    /// How many tracks past the current one are kept in the queue player.
    static let maxUpcoming = 20
    /// How long the page has to pick up a lock-screen command before it is carried out here.
    private static let remoteAckTimeout: TimeInterval = 0.5
    private static let cookieLifetime: TimeInterval = 30

    weak var delegate: AudioEngineDelegate?

    private let player = AVQueuePlayer()
    private var current: Entry?
    private var upcoming: [Entry] = []
    /// The URLs that started playing since the last load, in order; the last one is current.
    private var trail: [String] = []
    private var loadId = 0
    private var seq = 0
    private var wantsPlay = false
    private var rate: Float = 1
    /// The item the page asked for, so its arrival is not taken for the queue moving by itself.
    private var expectedItem: AVPlayerItem?
    /// The last item played to its end with nothing after it.
    private var endedAtTail = false
    private var interrupted = false
    /// A load is waiting for the web view's cookies: the queue player still holds the track
    /// before it, which must not be reported, or steered, as the one that was asked for.
    private var loadPending = false
    private var pendingPosition: Double = 0

    private var cookies: [HTTPCookie] = []
    private var cookiesFetchedAt = Date.distantPast
    private var cookieWaiters: [([HTTPCookie]) -> Void] = []

    private var observations: [NSKeyValueObservation] = []
    private var timeObserver: Any?
    private var backgroundTask: UIBackgroundTaskIdentifier = .invalid

    private var nextEnabledByPage = false
    private var previousEnabledByPage = false
    private var remoteId = 0
    private var pendingRemotes = Set<Int>()

    private var artwork: (url: URL, image: MPMediaItemArtwork)?
    private var artworkTask: URLSessionDataTask?

    override init() {
        super.init()
        player.automaticallyWaitsToMinimizeStalling = true
        observations.append(player.observe(\.timeControlStatus) { [weak self] _, _ in
            DispatchQueue.main.async { self?.playbackStatusChanged() }
        })
        observations.append(player.observe(\.currentItem) { [weak self] _, _ in
            DispatchQueue.main.async { self?.currentItemChanged() }
        })
        timeObserver = player.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 1, preferredTimescale: 10), queue: .main
        ) { [weak self] _ in
            self?.emitState()
        }

        let center = NotificationCenter.default
        center.addObserver(self, selector: #selector(itemFailedToPlayToEnd(_:)),
                           name: .AVPlayerItemFailedToPlayToEndTime, object: nil)
        center.addObserver(self, selector: #selector(sessionInterrupted(_:)),
                           name: AVAudioSession.interruptionNotification, object: nil)
        center.addObserver(self, selector: #selector(routeChanged(_:)),
                           name: AVAudioSession.routeChangeNotification, object: nil)

        configureSession()
        registerRemoteCommands()
    }

    deinit {
        if let timeObserver { player.removeTimeObserver(timeObserver) }
        NotificationCenter.default.removeObserver(self)
    }

    // MARK: - What the page asks for

    /// Replace whatever is loaded with `track`, at `position`, playing or not.
    func load(_ track: Track, position: Double, autoplay: Bool, loadId: Int, seq: Int) {
        self.loadId = loadId
        self.seq = seq
        trail = [track.url.absoluteString]
        wantsPlay = autoplay
        endedAtTail = false

        // The track asked for is the one already waiting behind the current one (Next, or a song's
        // end the page saw first): step onto it, buffered as it is, instead of loading it again.
        if position < 0.01, let next = upcoming.first, !next.failed, next.track.url == track.url {
            let items = player.items()
            // Already there when the song before it ended a moment ago, one behind otherwise.
            let arrived = items.first === next.item
            if arrived || items.dropFirst().first === next.item {
                upcoming.removeFirst()
                if let old = current { forget(old) }
                current = next
                expectedItem = next.item
                if !arrived { player.advanceToNextItem() }
                if autoplay { startPlayback() } else { player.pause() }
                trackChanged()
                return
            }
        }

        loadPending = true
        pendingPosition = position
        player.pause() // a new source silences the old one at once, as it does on an audio element
        withCookies { [weak self] cookies in
            guard let self, self.loadId == loadId else { return }
            self.loadPending = false
            self.replaceQueue(with: track, position: self.pendingPosition, cookies: cookies)
            if self.wantsPlay { self.startPlayback() } else { self.player.pause() }
            self.trackChanged()
        }
    }

    /// The tracks that follow the current one, in order. Sent again whenever the queue changes.
    func setUpcoming(_ tracks: [Track], loadId: Int) {
        guard loadId == self.loadId else { return }
        withCookies { [weak self] cookies in
            guard let self, self.loadId == loadId, let current = self.current else { return }
            let wanted = Array(tracks.prefix(Self.maxUpcoming))

            // Keep what already matches: the next item is usually buffered by now.
            var keep = 0
            while keep < wanted.count, keep < self.upcoming.count,
                  self.upcoming[keep].track.url == wanted[keep].url, !self.upcoming[keep].failed {
                keep += 1
            }
            for entry in self.upcoming[keep...] {
                self.player.remove(entry.item)
                self.forget(entry)
            }
            self.upcoming.removeSubrange(keep...)

            var last = self.upcoming.last?.item ?? current.item
            // After the tail ended the queue player holds nothing to insert behind.
            guard self.player.items().contains(last) else { return }
            for track in wanted[keep...] {
                let entry = self.makeEntry(track, cookies: cookies)
                guard self.player.canInsert(entry.item, after: last) else { break }
                self.player.insert(entry.item, after: last)
                self.upcoming.append(entry)
                last = entry.item
            }
            self.refreshRemoteCommands()
        }
    }

    func play(seq: Int) {
        self.seq = seq
        wantsPlay = true
        guard !loadPending, let current else { return }
        if player.currentItem == nil {
            // Played out, or failed: the queue player dropped it, so it is loaded again.
            reloadCurrent(current.track, position: 0, autoplay: true)
            return
        }
        startPlayback()
        emitState()
    }

    func pause(seq: Int) {
        self.seq = seq
        wantsPlay = false
        guard !loadPending else { return }
        player.pause()
        emitState()
        updateNowPlaying()
    }

    func seek(to time: Double, seq: Int) {
        self.seq = seq
        if loadPending {
            pendingPosition = max(0, time)
            return
        }
        guard let current else { return }
        if player.currentItem == nil {
            reloadCurrent(current.track, position: time, autoplay: wantsPlay)
            return
        }
        player.seek(to: CMTime(seconds: max(0, time), preferredTimescale: 1000),
                    toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            DispatchQueue.main.async {
                self?.emitState()
                self?.updateNowPlaying()
            }
        }
    }

    /// New words for the track that is loaded (its library row arrived after it started).
    func setMetadata(_ track: Track, loadId: Int) {
        guard loadId == self.loadId, let current, current.track.url == track.url else { return }
        current.track = track
        loadArtwork()
        updateNowPlaying()
    }

    func setRate(_ value: Float) {
        rate = value
        if #available(iOS 16.0, *) { player.defaultRate = value }
        if player.timeControlStatus != .paused { player.rate = value }
        updateNowPlaying()
    }

    func setVolume(_ value: Float) {
        player.volume = value
    }

    func stop() {
        loadId += 1
        loadPending = false
        wantsPlay = false
        player.pause()
        player.removeAllItems()
        if let current { forget(current) }
        upcoming.forEach(forget)
        current = nil
        upcoming = []
        trail = []
        endedAtTail = false
        endBackgroundTask()
        MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
        refreshRemoteCommands()
    }

    /// Which of Next and Previous the page can carry out (a station can supply a next track that
    /// is not in the queue yet).
    func setCommands(next: Bool, previous: Bool) {
        nextEnabledByPage = next
        previousEnabledByPage = previous
        refreshRemoteCommands()
    }

    /// The page picked a lock-screen command up, so it is not carried out here as well.
    func acknowledgeRemote(_ id: Int) {
        pendingRemotes.remove(id)
    }

    func state() -> [String: Any] {
        let item = player.currentItem
        let duration = item?.duration.seconds ?? .nan
        let position = player.currentTime().seconds
        // "paused" is someone's doing (Pause, a call, headphones coming out). Anything else that
        // keeps a wanted song silent is "waiting", which the page's stall watchdog acts on.
        let status: String
        if item == nil || !wantsPlay || interrupted {
            status = "paused"
        } else {
            status = player.timeControlStatus == .playing ? "playing" : "waiting"
        }
        return [
            "loadId": loadPending ? -1 : loadId,
            "seq": seq,
            "index": max(0, trail.count - 1),
            "trail": trail,
            "url": current?.track.url.absoluteString ?? "",
            "status": status,
            "position": position.isFinite ? max(0, position) : 0,
            "duration": duration.isFinite && duration > 0 ? duration : 0,
            "rate": Double(rate),
            "ended": endedAtTail
        ]
    }

    // MARK: - The queue

    private func replaceQueue(with track: Track, position: Double, cookies: [HTTPCookie]) {
        if let old = current { forget(old) }
        upcoming.forEach(forget)
        upcoming = []
        let entry = makeEntry(track, cookies: cookies)
        current = entry
        expectedItem = entry.item
        player.removeAllItems()
        player.insert(entry.item, after: nil)
        if position > 0 {
            entry.item.seek(to: CMTime(seconds: position, preferredTimescale: 1000),
                            toleranceBefore: .zero, toleranceAfter: .zero, completionHandler: nil)
        }
    }

    /// Put the current track back on the queue player without it counting as a new load.
    private func reloadCurrent(_ track: Track, position: Double, autoplay: Bool) {
        let loadId = self.loadId
        endedAtTail = false
        withCookies { [weak self] cookies in
            guard let self, self.loadId == loadId else { return }
            self.replaceQueue(with: track, position: position, cookies: cookies)
            if autoplay { self.startPlayback() } else { self.player.pause() }
            self.trackChanged()
        }
    }

    private func makeEntry(_ track: Track, cookies: [HTTPCookie]) -> Entry {
        let asset = AVURLAsset(url: track.url, options: [
            AVURLAssetHTTPCookiesKey: Self.cookies(cookies, for: track.url)
        ])
        let item = AVPlayerItem(asset: asset)
        item.audioTimePitchAlgorithm = .timeDomain // a slowed song keeps its pitch
        let entry = Entry(track: track, item: item)
        entry.statusObservation = item.observe(\.status) { [weak self, weak entry] item, _ in
            DispatchQueue.main.async {
                guard let self, let entry else { return }
                if item.status == .failed {
                    self.entryFailed(entry, error: item.error)
                } else if item.status == .readyToPlay, entry === self.current {
                    self.emitState() // the duration is known now
                    self.updateNowPlaying()
                }
            }
        }
        return entry
    }

    private func forget(_ entry: Entry) {
        entry.statusObservation?.invalidate()
        entry.statusObservation = nil
    }

    private func startPlayback() {
        endBackgroundTask()
        activateSession()
        player.play()
        if rate != 1 { player.rate = rate }
    }

    @objc private func itemFailedToPlayToEnd(_ notification: Notification) {
        guard let item = notification.object as? AVPlayerItem else { return }
        let error = notification.userInfo?[AVPlayerItemFailedToPlayToEndTimeErrorKey] as? Error
        DispatchQueue.main.async {
            guard let current = self.current, current.item === item else { return }
            self.entryFailed(current, error: error)
        }
    }

    private func currentItemChanged() {
        guard !loadPending else { return } // the queue is about to be replaced
        let item = player.currentItem
        if let item, item === expectedItem {
            expectedItem = nil
            return
        }
        guard let previous = current, previous.item !== item else { return }

        if let item, let next = upcoming.first, next.item === item {
            guard !Self.hasFailed(previous) else {
                // The queue moved on from a track that failed. Stop here and let the page decide
                // between a reload and a skip.
                player.pause()
                entryFailed(previous, error: previous.item.error)
                return
            }
            upcoming.removeFirst()
            forget(previous)
            current = next
            trail.append(next.track.url.absoluteString)
            trackChanged()
            return
        }

        if item == nil, !Self.hasFailed(previous), !endedAtTail {
            // Nothing follows in the queue. The page supplies what does (a station's next batch),
            // and it gets a moment in the background to do so.
            endedAtTail = true
            beginBackgroundTask()
            emit("ended", ["loadId": loadId, "index": max(0, trail.count - 1)])
            emitState()
        }
    }

    private static func hasFailed(_ entry: Entry) -> Bool {
        entry.failed || entry.item.status == .failed || entry.item.error != nil
    }

    private func entryFailed(_ entry: Entry, error: Error?) {
        guard !entry.failed else { return }
        entry.failed = true

        if entry === current {
            // Nothing may play on past a failed track before the page has had its say.
            for next in upcoming {
                player.remove(next.item)
                forget(next)
            }
            upcoming = []
            let nsError = error as NSError?
            emit("error", [
                "loadId": loadId,
                "index": max(0, trail.count - 1),
                "url": entry.track.url.absoluteString,
                "message": nsError?.localizedDescription ?? "Playback failed.",
                "kind": Self.isFormatError(nsError) ? "format" : "network"
            ])
            refreshRemoteCommands()
            return
        }

        // A track that failed while it waited its turn: drop it and everything behind it. The
        // song before it then ends at the tail, and the page loads this one itself and hears why.
        if let index = upcoming.firstIndex(where: { $0 === entry }) {
            for stale in upcoming[index...] {
                player.remove(stale.item)
                forget(stale)
            }
            upcoming.removeSubrange(index...)
            refreshRemoteCommands()
        }
    }

    /// A file AVFoundation could not make sense of, as opposed to one it could not fetch.
    private static func isFormatError(_ error: NSError?) -> Bool {
        guard let error else { return false }
        if error.domain == AVFoundationErrorDomain {
            let codes: [AVError.Code] = [
                .fileFormatNotRecognized, .fileFailedToParse, .failedToParse, .decoderNotFound,
                .decodeFailed, .undecodableMediaData, .formatUnsupported
            ]
            return codes.contains { $0.rawValue == error.code }
        }
        if let underlying = error.userInfo[NSUnderlyingErrorKey] as? NSError {
            return isFormatError(underlying)
        }
        return false
    }

    // MARK: - Reporting

    private func emit(_ event: String, _ data: [String: Any]) {
        delegate?.audioEngine(self, didEmit: event, data: data)
    }

    private func emitState() {
        guard current != nil, !loadPending else { return }
        emit("state", state())
    }

    private func playbackStatusChanged() {
        emitState()
        updateNowPlaying()
    }

    private func trackChanged() {
        refreshRemoteCommands()
        loadArtwork()
        updateNowPlaying()
        emitState()
    }

    // MARK: - Cookies

    private func withCookies(_ body: @escaping ([HTTPCookie]) -> Void) {
        if cookieWaiters.isEmpty, Date().timeIntervalSince(cookiesFetchedAt) < Self.cookieLifetime {
            body(cookies)
            return
        }
        cookieWaiters.append(body)
        guard cookieWaiters.count == 1 else { return } // a fetch is on its way
        guard let delegate else {
            flushCookieWaiters()
            return
        }
        delegate.audioEngineCookies(self) { [weak self] fetched in
            DispatchQueue.main.async {
                guard let self else { return }
                self.cookies = fetched
                self.cookiesFetchedAt = Date()
                self.flushCookieWaiters()
            }
        }
    }

    private func flushCookieWaiters() {
        let waiters = cookieWaiters
        cookieWaiters = []
        waiters.forEach { $0(cookies) }
    }

    /// The cookies a request to `url` would carry, and no others: a cover on someone else's CDN
    /// must never be sent the session.
    private static func cookies(_ all: [HTTPCookie], for url: URL) -> [HTTPCookie] {
        guard let host = url.host?.lowercased() else { return [] }
        return all.filter { cookie in
            if cookie.isSecure, url.scheme?.lowercased() != "https" { return false }
            let domain = cookie.domain.lowercased()
            if domain.hasPrefix(".") {
                let bare = String(domain.dropFirst())
                return host == bare || host.hasSuffix(domain)
            }
            return host == domain
        }
    }

    // MARK: - The audio session

    private func configureSession() {
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .default)
    }

    private func activateSession() {
        let session = AVAudioSession.sharedInstance()
        if session.category != .playback { configureSession() }
        try? session.setActive(true)
    }

    @objc private func sessionInterrupted(_ notification: Notification) {
        guard let raw = notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
              let type = AVAudioSession.InterruptionType(rawValue: raw) else { return }
        DispatchQueue.main.async {
            switch type {
            case .began:
                self.interrupted = true
                self.emitState()
            case .ended:
                guard self.interrupted else { return }
                self.interrupted = false
                let rawOptions = notification.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt
                let options = AVAudioSession.InterruptionOptions(rawValue: rawOptions ?? 0)
                if options.contains(.shouldResume), self.wantsPlay, self.player.currentItem != nil {
                    self.startPlayback()
                } else {
                    self.wantsPlay = false // iOS says to stay quiet: a pause, not a stall
                }
                self.emitState()
                self.updateNowPlaying()
            @unknown default:
                break
            }
        }
    }

    @objc private func routeChanged(_ notification: Notification) {
        guard let raw = notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt,
              AVAudioSession.RouteChangeReason(rawValue: raw) == .oldDeviceUnavailable else { return }
        // Headphones came out: music must not carry on from the speaker.
        DispatchQueue.main.async {
            self.wantsPlay = false
            self.player.pause()
            self.emitState()
            self.updateNowPlaying()
        }
    }

    private func beginBackgroundTask() {
        guard backgroundTask == .invalid else { return }
        backgroundTask = UIApplication.shared.beginBackgroundTask(withName: "next-track") { [weak self] in
            self?.endBackgroundTask()
        }
    }

    private func endBackgroundTask() {
        guard backgroundTask != .invalid else { return }
        UIApplication.shared.endBackgroundTask(backgroundTask)
        backgroundTask = .invalid
    }

    // MARK: - Lock screen, Control Center, headsets

    private func registerRemoteCommands() {
        let commands = MPRemoteCommandCenter.shared()
        commands.playCommand.addTarget { [weak self] _ in self?.remote("play") ?? .commandFailed }
        commands.pauseCommand.addTarget { [weak self] _ in self?.remote("pause") ?? .commandFailed }
        commands.togglePlayPauseCommand.addTarget { [weak self] _ in
            self?.remote("toggle") ?? .commandFailed
        }
        commands.nextTrackCommand.addTarget { [weak self] _ in self?.remote("next") ?? .commandFailed }
        commands.previousTrackCommand.addTarget { [weak self] _ in
            self?.remote("previous") ?? .commandFailed
        }
        commands.changePlaybackPositionCommand.addTarget { [weak self] event in
            guard let event = event as? MPChangePlaybackPositionCommandEvent else {
                return .commandFailed
            }
            return self?.remote("seek", time: event.positionTime) ?? .commandFailed
        }
        // Either the track pair or the seek pair shows, never both: keep the track pair.
        commands.skipForwardCommand.isEnabled = false
        commands.skipBackwardCommand.isEnabled = false
        refreshRemoteCommands()
    }

    private func refreshRemoteCommands() {
        let commands = MPRemoteCommandCenter.shared()
        let loaded = current != nil
        commands.playCommand.isEnabled = loaded
        commands.pauseCommand.isEnabled = loaded
        commands.togglePlayPauseCommand.isEnabled = loaded
        commands.changePlaybackPositionCommand.isEnabled = loaded
        commands.nextTrackCommand.isEnabled = loaded && (nextEnabledByPage || !upcoming.isEmpty)
        commands.previousTrackCommand.isEnabled = loaded && previousEnabledByPage
    }

    /// Hand a command to the page, which knows what it means for the account's session; carry it
    /// out here only when the page does not answer (it is asleep).
    private func remote(_ command: String, time: Double? = nil) -> MPRemoteCommandHandlerStatus {
        guard current != nil else { return .noActionableNowPlayingItem }
        remoteId += 1
        let id = remoteId
        pendingRemotes.insert(id)
        var data: [String: Any] = ["id": id, "command": command]
        if let time { data["time"] = time }
        emit("remote", data)
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.remoteAckTimeout) { [weak self] in
            guard let self, self.pendingRemotes.remove(id) != nil else { return }
            self.carryOut(command, time: time)
        }
        return .success
    }

    private func carryOut(_ command: String, time: Double?) {
        switch command {
        case "play":
            play(seq: seq)
        case "pause":
            pause(seq: seq)
        case "toggle":
            if wantsPlay { pause(seq: seq) } else { play(seq: seq) }
        case "next":
            guard !upcoming.isEmpty, player.currentItem != nil else { return }
            player.advanceToNextItem()
            if wantsPlay { startPlayback() }
        case "previous":
            seek(to: 0, seq: seq)
        case "seek":
            if let time { seek(to: time, seq: seq) }
        default:
            break
        }
    }

    private func updateNowPlaying() {
        guard let current else { return }
        let track = current.track
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: track.title,
            MPMediaItemPropertyArtist: track.artist,
            MPMediaItemPropertyAlbumTitle: track.album,
            MPNowPlayingInfoPropertyMediaType: MPNowPlayingInfoMediaType.audio.rawValue,
            MPNowPlayingInfoPropertyDefaultPlaybackRate: 1.0,
            MPNowPlayingInfoPropertyPlaybackRate:
                player.timeControlStatus == .playing ? Double(rate) : 0.0
        ]
        let position = player.currentTime().seconds
        if position.isFinite {
            info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = max(0, position)
        }
        let duration = current.item.duration.seconds
        if duration.isFinite, duration > 0 {
            info[MPMediaItemPropertyPlaybackDuration] = duration
        }
        if let artwork, artwork.url == track.artworkUrl {
            info[MPMediaItemPropertyArtwork] = artwork.image
        }
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info
    }

    private func loadArtwork() {
        guard let url = current?.track.artworkUrl else {
            artworkTask?.cancel()
            artwork = nil
            return
        }
        if artwork?.url == url { return }
        artworkTask?.cancel()
        withCookies { [weak self] cookies in
            guard let self, self.current?.track.artworkUrl == url else { return }
            var request = URLRequest(url: url)
            request.httpShouldHandleCookies = false
            for (name, value) in HTTPCookie.requestHeaderFields(with: Self.cookies(cookies, for: url)) {
                request.setValue(value, forHTTPHeaderField: name)
            }
            let task = URLSession.shared.dataTask(with: request) { [weak self] data, _, _ in
                guard let data, let image = UIImage(data: data) else { return }
                let art = MPMediaItemArtwork(boundsSize: image.size) { _ in image }
                DispatchQueue.main.async {
                    guard let self, self.current?.track.artworkUrl == url else { return }
                    self.artwork = (url, art)
                    self.updateNowPlaying()
                }
            }
            self.artworkTask = task
            task.resume()
        }
    }
}
