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
        XCTAssertTrue(p720.waitForExistence(timeout: 15), app.debugDescription)
        XCTAssertTrue(p720.isSelected)
        XCTAssertTrue(fps.isSelected)
    }

    func testSettingsControls() {
        app.buttons["Settings"].tap()
        let controls = app.segmentedControls
        XCTAssertGreaterThanOrEqual(controls.count, 10)
        var checked: [String] = []
        for index in 0..<controls.count {
            let control = controls.element(boundBy: index)
            let choices = control.buttons.allElementsBoundByIndex
            guard let original = choices.first(where: { $0.isSelected }) else { continue }
            // Scroll inside the settings drawer, keeping the host list stationary.
            for _ in 0..<12 where !original.isHittable {
                let pane = app.scrollViews.containing(.segmentedControl, identifier: nil).firstMatch
                guard pane.exists else { break }
                pane.swipeUp()
            }
            guard original.isHittable else { continue }
            for choice in choices where choice.isHittable && choice.isEnabled && !choice.isSelected {
                let name = choice.label
                choice.tap()
                let alert = app.alerts.firstMatch
                if alert.exists {
                    if alert.buttons["Cancel"].exists { alert.buttons["Cancel"].tap() }
                    else if alert.buttons["OK"].exists { alert.buttons["OK"].tap() }
                }
                if name != "Custom" { XCTAssertTrue(choice.isSelected, "Setting: " + name) }
                checked.append("\(index):\(name)")
            }
            if original.isHittable && !original.isSelected { original.tap() }
        }
        let output = XCTAttachment(string: checked.joined(separator: "\n"))
        output.name = "Settings options exercised"
        output.lifetime = .keepAlways
        add(output)
        XCTAssertGreaterThan(checked.count, 10)
    }
    func testUploadReceipt() {
        app.buttons["日志"].tap()
        app.alerts.buttons["上传"].tap()
        XCTAssertTrue(app.alerts["上传成功"].waitForExistence(timeout: 30), app.debugDescription)
        app.alerts.buttons["好"].tap()
    }

    func testPairingAndApplicationLibrary() throws {
        let label = "PiuLink Simulator Host"
        let host = app.staticTexts[label].firstMatch
        if !host.waitForExistence(timeout: 4) {
            let tile = app.descendants(matching: .any).matching(NSPredicate(format: "label == %@", "Add Host Manually")).firstMatch
            tile.tap()
            let field = app.alerts.textFields.firstMatch
            field.tap()
            field.typeText("127.0.0.1")
            app.alerts.buttons["OK"].tap()
            XCTAssertTrue(host.waitForExistence(timeout: 25), app.debugDescription)
        }
        host.tap()
        let alert = app.alerts["Pairing"]
        XCTAssertTrue(alert.waitForExistence(timeout: 15))
        let message = alert.staticTexts.allElementsBoundByIndex.map(\.label).joined(separator: " ")
        let range = try XCTUnwrap(message.range(of: "[0-9]{4}", options: .regularExpression))
        var request = URLRequest(url: URL(string: "http://127.0.0.1:18589/pin")!)
        request.httpMethod = "POST"
        request.httpBody = Data(message[range].utf8)
        let response = expectation(description: "Host accepted the PIN")
        URLSession.shared.dataTask(with: request) { _, result, _ in
            XCTAssertEqual((result as? HTTPURLResponse)?.statusCode, 204)
            response.fulfill()
        }.resume()
        wait(for: [response], timeout: 15)
        let desktop = app.buttons["Desktop"].firstMatch
        XCTAssertTrue(desktop.waitForExistence(timeout: 30), app.debugDescription)
        desktop.tap()
        sleep(7)
        let shot = XCTAttachment(screenshot: app.screenshot())
        shot.name = "Desktop stream attempt"
        shot.lifetime = .keepAlways
        add(shot)
    }
}


