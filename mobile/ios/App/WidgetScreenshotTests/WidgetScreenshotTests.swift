import XCTest

final class WidgetScreenshotTests: XCTestCase {
    private let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")

    override func setUpWithError() throws {
        continueAfterFailure = false
    }

    func testWidgetGalleryAndHomeScreen() {
        let app = XCUIApplication(bundleIdentifier: "com.musichoarder.app")
        for appearance in ["Light"] {
            app.launchEnvironment["MH_WIDGET_SHOTS"] = "1"
            app.launch()
            sleep(8)
            XCUIDevice.shared.press(.home)
            sleep(3)
            openGallery()
            for (index, name) in ["now-playing-small", "now-playing-medium", "last-played-medium", "last-played-large"].enumerated() {
                if index > 0 {
                    springboard.swipeLeft()
                    sleep(2)
                }
                capture("ios-\(name)-\(appearance.lowercased())")
            }
            tap("Add Widget", in: springboard)
            sleep(3)
            if springboard.buttons["Done"].exists { springboard.buttons["Done"].tap() }
            sleep(3)
            capture("ios-home-\(appearance.lowercased())")
            app.terminate()
        }
    }

    private func openGallery() {
        springboard.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.72)).press(forDuration: 1.5)
        capture("home-held")
        if springboard.buttons["Edit Home Screen"].exists { springboard.buttons["Edit Home Screen"].tap() }
        if springboard.buttons["Edit"].waitForExistence(timeout: 5) { springboard.buttons["Edit"].tap() }
        capture("home-edit")
        tap("Add Widget", in: springboard)
        let search = springboard.searchFields.firstMatch
        XCTAssertTrue(search.waitForExistence(timeout: 15), "Widget gallery search is visible")
        search.tap()
        search.typeText("MusicHoarder")
        sleep(3)
        capture("gallery-search")
        let result = springboard.cells["MusicHoarder"].firstMatch
        XCTAssertTrue(result.waitForExistence(timeout: 10), "MusicHoarder is offered in the widget gallery")
        result.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).tap()
        sleep(3)
    }

    private func tap(_ label: String, in app: XCUIApplication) {
        let match = NSPredicate(format: "label MATCHES %@", "\\s*" + NSRegularExpression.escapedPattern(for: label) + "\\s*")
        let button = app.buttons.matching(match).firstMatch
        if button.waitForExistence(timeout: 5) && button.isHittable {
            button.tap()
            return
        }
        let text = app.staticTexts.matching(match).firstMatch
        XCTAssertTrue(text.waitForExistence(timeout: 5), "\(label) is visible")
        text.tap()
    }

    private func capture(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
        let hierarchy = XCTAttachment(string: springboard.debugDescription)
        hierarchy.name = name + "-hierarchy"
        hierarchy.lifetime = .keepAlways
        add(hierarchy)
    }
}
