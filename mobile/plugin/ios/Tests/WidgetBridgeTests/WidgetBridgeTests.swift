import XCTest
@testable import WidgetBridge

final class WidgetBridgeTests: XCTestCase {
    private let art = ArtFile(sha256Hex: String(repeating: "ab", count: 32))
    private let asOf = Date(timeIntervalSince1970: 1_763_337_600)

    private func transport(playing: Bool, position: Double = 12.5, duration: Double = 201,
                           rate: Double = 1) -> Transport {
        Transport(playing: playing, position: position, duration: duration, rate: rate,
                  asOf: asOf, canNext: true, canPrevious: false)
    }

    private func track(_ reach: Reach) -> NowPlaying {
        .track(TrackInfo(title: "Song", artist: "Artist", album: "Album", art: art), reach)
    }

    private func temporaryStore() -> WidgetStore {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("widget-bridge-\(UUID().uuidString)", isDirectory: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return WidgetStore(root: root)
    }

    // MARK: - The documents

    func test_nowPlaying_survivesItsOwnJson() {
        for value in [.nothing, track(.needsApp), track(.background(transport(playing: true)))] {
            XCTAssertEqual(NowPlaying(json: value.json), value)
        }
    }

    func test_nowPlaying_writesTheContractsKeys() throws {
        let object = try XCTUnwrap(JSONSerialization.jsonObject(
            with: track(.background(transport(playing: true))).json) as? [String: Any])
        XCTAssertEqual(object["v"] as? Int, 1)
        XCTAssertEqual(object["state"] as? String, "track")
        XCTAssertEqual(object["reach"] as? String, "background")
        XCTAssertEqual(object["art"] as? String, "art/\(String(repeating: "ab", count: 32)).jpg")
        let stored = try XCTUnwrap(object["transport"] as? [String: Any])
        XCTAssertEqual(Set(stored.keys),
                       ["playing", "position", "duration", "rate", "asOf", "canNext", "canPrevious"])
        XCTAssertEqual((stored["asOf"] as? NSNumber)?.int64Value, 1_763_337_600_000)
    }

    func test_nowPlaying_readsAnythingElseAsNothing() {
        XCTAssertEqual(NowPlaying(json: Data("not json".utf8)), .nothing)
        XCTAssertEqual(NowPlaying(json: Data(#"{"v":2,"state":"track","title":"x"}"#.utf8)), .nothing)
        XCTAssertEqual(NowPlaying(json: Data(#"{"v":1,"state":"nothing"}"#.utf8)), .nothing)
    }

    func test_nowPlaying_withoutATransportNeedsTheApp() {
        let json = Data(#"{"v":1,"state":"track","title":"Song","reach":"background"}"#.utf8)
        XCTAssertEqual(NowPlaying(json: json),
                       .track(TrackInfo(title: "Song", artist: "", album: "", art: nil), .needsApp))
    }

    func test_art_refusesANameOutsideItsFolder() {
        XCTAssertNil(ArtFile(relativePath: "art/../now-playing.json"))
        XCTAssertNil(ArtFile(relativePath: "/etc/passwd"))
        XCTAssertNil(ArtFile(sha256Hex: "abc"))
        XCTAssertNotNil(art)
    }

    func test_shelf_survivesItsOwnJson_andHoldsEight() throws {
        let items = try (0..<10).map { index in
            try XCTUnwrap(ShelfItem(id: "album \(index)/é", title: "Title", subtitle: "Artist",
                                    art: index == 0 ? art : nil, path: "/album/\(index)"))
        }
        let shelf = Shelf(items: items)
        XCTAssertEqual(shelf.items.count, 8)
        XCTAssertEqual(Shelf(json: shelf.json), shelf)
    }

    func test_shelf_dropsAPathThatLeavesTheApp() {
        XCTAssertNil(ShelfItem(id: "a", title: "", subtitle: "", art: nil, path: "//evil.example"))
        XCTAssertNil(ShelfItem(id: "a", title: "", subtitle: "", art: nil, path: "https://evil.example"))
        XCTAssertNil(ShelfItem(id: "", title: "", subtitle: "", art: nil, path: "/album/1"))
        let json = Data(#"{"v":1,"items":[{"id":"a","title":"","subtitle":"","path":"//evil.example"}]}"#.utf8)
        XCTAssertEqual(Shelf(json: json), .empty)
    }

    // MARK: - The store

    func test_store_readsNothingBeforeTheFirstWrite() {
        let store = temporaryStore()
        XCTAssertEqual(store.readNowPlaying(), .nothing)
        XCTAssertEqual(store.readShelf(), .empty)
    }

    func test_store_skipsAWriteThatSaysTheSame() {
        let store = temporaryStore()
        XCTAssertFalse(store.write(.nothing))
        XCTAssertTrue(store.write(track(.background(transport(playing: true)))))

        // The same player two seconds on: nothing a widget shows has changed.
        let later = Transport(playing: true, position: 14.5, duration: 201, rate: 1,
                              asOf: asOf.addingTimeInterval(2), canNext: true, canPrevious: false)
        XCTAssertFalse(store.write(track(.background(later))))

        // A seek has.
        let sought = Transport(playing: true, position: 90, duration: 201, rate: 1,
                               asOf: asOf.addingTimeInterval(2), canNext: true, canPrevious: false)
        XCTAssertTrue(store.write(track(.background(sought))))
        XCTAssertTrue(store.write(track(.background(transport(playing: false)))))
    }

    func test_store_demotesOnce() {
        let store = temporaryStore()
        store.write(track(.background(transport(playing: true))))
        XCTAssertTrue(store.demoteNowPlaying())
        XCTAssertEqual(store.readNowPlaying(), track(.needsApp))
        XCTAssertFalse(store.demoteNowPlaying())
    }

    func test_store_prunesCoversNoDocumentNames() throws {
        let store = temporaryStore()
        let named = try XCTUnwrap(art)
        let kept = try XCTUnwrap(ArtFile(sha256Hex: String(repeating: "cd", count: 32)))
        let stale = try XCTUnwrap(ArtFile(sha256Hex: String(repeating: "ef", count: 32)))
        for file in [named, kept, stale] { XCTAssertTrue(store.storeArt(Data([1]), as: file)) }
        store.write(track(.needsApp))

        store.pruneArt(keeping: [kept])

        XCTAssertTrue(store.hasArt(named))
        XCTAssertTrue(store.hasArt(kept))
        XCTAssertFalse(store.hasArt(stale))
    }

    func test_store_clearLeavesNothing() throws {
        let store = temporaryStore()
        let file = try XCTUnwrap(art)
        store.storeArt(Data([1]), as: file)
        store.write(track(.needsApp))
        store.clear()
        XCTAssertEqual(store.readNowPlaying(), .nothing)
        XCTAssertFalse(store.hasArt(file))
    }

    // MARK: - The timeline

    func test_timeline_pausedPlayerIsTrustedForHalfAnHour() {
        let snapshot = track(.background(transport(playing: false)))
        let entries = NowPlayingTimeline.entries(for: snapshot, now: asOf.addingTimeInterval(60))
        XCTAssertEqual(entries.map(\.nowPlaying), [snapshot, track(.needsApp)])
        XCTAssertEqual(entries.last?.date, asOf.addingTimeInterval(30 * 60))

        let late = NowPlayingTimeline.entries(for: snapshot, now: asOf.addingTimeInterval(31 * 60))
        XCTAssertEqual(late.map(\.nowPlaying), [track(.needsApp)])
    }

    func test_timeline_playingSongLapsesAfterItsEnd() {
        // 188.5 s left at double speed: the song ends 94.25 s after the snapshot.
        let snapshot = track(.background(transport(playing: true, rate: 2)))
        let entries = NowPlayingTimeline.entries(for: snapshot, now: asOf)
        XCTAssertEqual(entries.map(\.nowPlaying), [snapshot, track(.needsApp)])
        XCTAssertEqual(entries.last?.date, asOf.addingTimeInterval(94.25 + 90))
    }

    func test_timeline_otherStatesAreOneEntry() {
        for snapshot in [.nothing, track(.needsApp)] {
            XCTAssertEqual(NowPlayingTimeline.entries(for: snapshot, now: asOf).map(\.nowPlaying),
                           [snapshot])
        }
    }

    // MARK: - Links and the mailbox

    func test_link_survivesItsOwnUrl() {
        for link in [WidgetLink.nowPlaying, .resume, .shelfItem(id: "Artist/Álbum #1?x=y")] {
            XCTAssertEqual(WidgetLink(link.url), link)
        }
        XCTAssertEqual(WidgetLink.resume.url.absoluteString, "musichoarder://widget/resume")
    }

    func test_link_refusesEverythingElse() throws {
        for text in ["musichoarder://auth?token=t", "musichoarder://widget/", "https://widget/resume",
                     "musichoarder://widget/resume?path=/x", "musichoarder://widget/shelf/",
                     "musichoarder://widget/shelf/a/b", "musichoarder://widget/open/overview"] {
            XCTAssertNil(WidgetLink(try XCTUnwrap(URL(string: text))), text)
        }
    }

    func test_link_shelfIdResolvesToThePagesOwnPath() throws {
        let shelf = Shelf(items: [try XCTUnwrap(
            ShelfItem(id: "a1", title: "", subtitle: "", art: nil, path: "/album/a1"))])
        XCTAssertEqual(WidgetLink.shelfItem(id: "a1").action(in: shelf), .open(path: "/album/a1"))
        XCTAssertNil(WidgetLink.shelfItem(id: "gone").action(in: shelf))
        XCTAssertEqual(WidgetLink.resume.action(in: .empty), .resume)
    }

    func test_mailbox_handsAnActionOverOnce_andNotALateOne() {
        let mailbox = LaunchMailbox()
        var nudges = 0
        mailbox.onPut = { nudges += 1 }

        mailbox.put(.nowPlaying, now: asOf)
        mailbox.put(.resume, now: asOf)
        XCTAssertEqual(nudges, 2)
        XCTAssertEqual(mailbox.take(now: asOf.addingTimeInterval(5)), .resume)
        XCTAssertNil(mailbox.take(now: asOf.addingTimeInterval(5)))

        mailbox.put(.resume, now: asOf)
        XCTAssertNil(mailbox.take(now: asOf.addingTimeInterval(31)))
    }

    // MARK: - The router

    func test_router_withoutAPlayerDemotesAndAsksForTheApp() {
        let store = temporaryStore()
        store.write(track(.background(transport(playing: false))))
        var outcome: CommandOutcome?
        WidgetCommandRouter(store: store).send(.play) { outcome = $0 }
        XCTAssertEqual(outcome, .needsApp)
        XCTAssertEqual(store.readNowPlaying(), track(.needsApp))
    }

    func test_router_reportsWhatThePlayerDid() {
        let store = temporaryStore()
        let snapshot = track(.background(transport(playing: false)))
        store.write(snapshot)
        let router = WidgetCommandRouter(store: store)
        var asked: [WidgetCommand] = []
        var outcomes: [CommandOutcome] = []

        router.player = { command, done in asked.append(command); done(true) }
        router.send(.next) { outcomes.append($0) }
        XCTAssertEqual(store.readNowPlaying(), snapshot)

        router.player = { command, done in asked.append(command); done(false) }
        router.send(.play) { outcomes.append($0) }

        XCTAssertEqual(asked, [.next, .play])
        XCTAssertEqual(outcomes, [.carriedOut, .needsApp])
        XCTAssertEqual(store.readNowPlaying(), track(.needsApp))
    }
}
