import AppKit


final class TargetWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}
final class TargetView: NSView {
    let marker: URL
    var index = 0
    let colors = [NSColor.systemPink, .systemTeal, .systemYellow]

    init(frame: NSRect, marker: URL) {
        self.marker = marker
        super.init(frame: frame)
    }
    required init?(coder: NSCoder) { fatalError() }
    override func draw(_ dirtyRect: NSRect) {
        colors[index % colors.count].setFill()
        bounds.fill()
        ("PiuLink simulator media check  " + String(index) as NSString).draw(at: NSPoint(x: 60, y: bounds.midY), withAttributes: [.font: NSFont.systemFont(ofSize: 52), .foregroundColor: NSColor.black])
    }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func keyDown(with event: NSEvent) {
        if event.charactersIgnoringModifiers?.lowercased() == "p" {
            try? Data("p".utf8).write(to: marker.deletingLastPathComponent().appendingPathComponent("key-received"))
        }
    }
    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
        window?.makeFirstResponder(self)
        try? Data("click".utf8).write(to: marker)
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let screen = NSScreen.main!
let window = TargetWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
let target = TargetView(frame: screen.frame, marker: URL(fileURLWithPath: CommandLine.arguments[1]))
window.contentView = target
window.level = .screenSaver
window.orderFrontRegardless()
Timer.scheduledTimer(withTimeInterval: 0.8, repeats: true) { _ in
    target.index += 1
    target.needsDisplay = true
}
app.run()
