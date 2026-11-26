import XCTest

final class ClientChecks: XCTestCase {
    let app = XCUIApplication(bundleIdentifier: "labs.atipiu.piulink.ios")

    override func setUpWithError() throws {
        continueAfterFailure = false
        XCUIDevice.shared.orientation = .landscapeLeft
        app.launch()
        XCTAssertTrue(app.buttons["Settings"].waitForExistence(timeout: 20))
    }

    func testDiagnosticsCancel() {
        app.buttons["日志"].tap()
        let alert = app.alerts.firstMatch
        XCTAssertTrue(alert.waitForExistence(timeout: 10), app.debugDescription)
        XCTAssertTrue(alert.buttons["上传"].exists)
        alert.buttons["取消"].tap()
        XCTAssertFalse(alert.exists)
    }

    func testManualHostCancel() {
        let tile = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", "Add Host Manually")).firstMatch
        XCTAssertTrue(tile.waitForExistence(timeout: 10))
        tile.tap()
        XCTAssertTrue(app.alerts.firstMatch.textFields.firstMatch.waitForExistence(timeout: 10))
        app.alerts.buttons["Cancel"].tap()
        XCTAssertFalse(app.alerts.firstMatch.exists)
    }

    func testSettingsPersist() {
        app.buttons["Settings"].tap()
        let p720 = app.buttons["720p"]
        let fps = app.buttons["30 FPS"]
        XCTAssertTrue(p720.waitForExistence(timeout: 10), app.debugDescription)
        p720.tap()
        fps.tap()
        app.buttons["Settings"].tap()
        app.terminate()
        app.launch()
        app.buttons["Settings"].tap()
        XCTAssertTrue(p720.isSelected)
        XCTAssertTrue(fps.isSelected)
    }
}
