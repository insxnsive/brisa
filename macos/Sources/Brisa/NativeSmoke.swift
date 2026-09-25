import AppKit
import Foundation

/// CI acceptance of the actual native window. No account/network action is submitted.
@MainActor
enum NativeSmoke {
    private static var started = false
    private enum Failure: Error { case assertion(String) }

    static func start(model: AppModel) {
        guard !started, let index = CommandLine.arguments.firstIndex(of: "--smoke-no-network") else { return }
        started = true
        guard CommandLine.arguments.indices.contains(index + 1) else { exit(64) }
        let output = URL(fileURLWithPath: CommandLine.arguments[index + 1], isDirectory: true)
        DispatchQueue.global().asyncAfter(deadline: .now() + 35) { exit(70) }
        Task {
            do {
                try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
                try await run(model: model, output: output)
                try requestQuit()
            } catch {
                let report: [String: Any] = ["passed": false, "error": String(describing: error)]
                if let data = try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted]) {
                    try? data.write(to: output.appendingPathComponent("results.json"))
                }
                fputs("Native window acceptance failed: \(error)\n", stderr)
                exit(1)
            }
        }
    }

    private static func pause() async throws { try await Task.sleep(nanoseconds: 180_000_000) }
    private static func requestQuit() throws {
        guard let window = NSApplication.shared.keyWindow else { throw Failure.assertion("No key window for Command-Q") }
        // terminateLater runs a nested AppKit loop. Invoke Quit from a real event,
        // not this main-actor task, so its asynchronous cleanup can be scheduled.
        for kind in [NSEvent.EventType.keyDown, .keyUp] {
            guard let event = NSEvent.keyEvent(with: kind, location: .zero, modifierFlags: .command,
                                               timestamp: ProcessInfo.processInfo.systemUptime,
                                               windowNumber: window.windowNumber, context: nil,
                                               characters: "q", charactersIgnoringModifiers: "q",
                                               isARepeat: false, keyCode: 12) else {
                throw Failure.assertion("Could not create Command-Q event")
            }
            NSApplication.shared.postEvent(event, atStart: false)
        }
    }
    private static func require(_ condition: Bool, _ message: String) throws {
        if !condition { throw Failure.assertion(message) }
    }
    private static func waitFor(_ message: String, until condition: () -> Bool) async throws {
        for _ in 0..<25 {
            if condition() { try await pause(); return }
            try await pause()
        }
        try require(condition(), message)
    }
    private static func views(in window: NSWindow) -> [NSView] {
        func visit(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(visit) }
        guard let root = window.contentView else { return [] }
        return visit(root).filter { !$0.isHiddenOrHasHiddenAncestor && $0.bounds.width > 0 && $0.bounds.height > 0 }
    }
    private static func point(_ id: String, in window: NSWindow) throws -> NSPoint {
        let anchors = views(in: window).filter { $0.identifier?.rawValue == id }
        try require(anchors.count == 1, "Expected one native anchor for \(id); found \(anchors.count)")
        let rect = anchors[0].convert(anchors[0].bounds, to: nil)
        return NSPoint(x: rect.midX, y: rect.midY)
    }
    private static func button(_ id: String, in window: NSWindow) throws -> NSButton {
        let location = try point(id, in: window)
        let controls = views(in: window).compactMap { $0 as? NSButton }
            .filter { $0.convert($0.bounds, to: nil).contains(location) }
        try require(controls.count == 1, "Expected one actual NSButton at \(id); found \(controls.count)")
        return controls[0]
    }
    private static func press(_ id: String, in window: NSWindow) throws {
        let location = try point(id, in: window)
        // Plain SwiftUI buttons are not NSButtons. Send genuine mouse events
        // through the owned window instead of activating a neighbouring control.
        for kind in [NSEvent.EventType.leftMouseDown, .leftMouseUp] {
            guard let event = NSEvent.mouseEvent(with: kind, location: location, modifierFlags: [],
                                                 timestamp: ProcessInfo.processInfo.systemUptime,
                                                 windowNumber: window.windowNumber, context: nil,
                                                 eventNumber: 0, clickCount: 1, pressure: kind == .leftMouseDown ? 1 : 0) else {
                throw Failure.assertion("Could not create native click for \(id)")
            }
            NSApplication.shared.postEvent(event, atStart: false)
        }
    }
    private static func type(_ text: String, into id: String, window: NSWindow) throws {
        let location = try point(id, in: window)
        let fields = views(in: window).compactMap { $0 as? NSTextField }
            .filter { $0.isEditable && $0.convert($0.bounds, to: nil).contains(location) }
        try require(fields.count == 1, "Expected one native text field at \(id); found \(fields.count)")
        let field = fields[0]
        field.selectText(nil)
        guard let editor = field.currentEditor() as? NSTextView else { throw Failure.assertion("Native field did not accept focus") }
        editor.selectAll(nil)
        editor.insertText(text, replacementRange: NSRange(location: NSNotFound, length: 0))
        window.makeFirstResponder(nil)
    }
    private static func capture(_ name: String, window: NSWindow, output: URL) throws {
        window.contentView?.layoutSubtreeIfNeeded()
        window.contentView?.displayIfNeeded()
        guard let image = CGWindowListCreateImage(.null, .optionIncludingWindow,
                                                  CGWindowID(window.windowNumber), [.boundsIgnoreFraming, .bestResolution]) else {
            throw Failure.assertion("No image for the owned application window")
        }
        let bitmap = NSBitmapImageRep(cgImage: image)
        guard let data = bitmap.representation(using: .png, properties: [:]) else {
            throw Failure.assertion("PNG encoding failed")
        }
        try data.write(to: output.appendingPathComponent(name + ".png"))
        if name.hasPrefix("light-") || name.hasPrefix("dark-") {
            guard let surface = bitmap.colorAt(x: bitmap.pixelsWide / 2, y: bitmap.pixelsHigh * 3 / 4)?.usingColorSpace(.sRGB) else {
                throw Failure.assertion("Could not inspect rendered surface for \(name)")
            }
            let luminance = surface.redComponent * 0.2126 + surface.greenComponent * 0.7152 + surface.blueComponent * 0.0722
            try require(name.hasPrefix("dark-") ? luminance < 0.35 : luminance > 0.65,
                        "Rendered surface does not match \(name) appearance: \(luminance)")
        }
    }
    private static func run(model: AppModel, output: URL) async throws {
        for _ in 0..<25 {
            if NSApplication.shared.windows.contains(where: { $0.isVisible && $0.contentView != nil }) { break }
            try await pause()
        }
        let windows = NSApplication.shared.windows.filter { $0.isVisible && $0.contentView != nil }
        try require(windows.count == 1, "Expected exactly one real application window")
        let window = windows[0]
        window.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
        try await pause()
        try? capture("startup-debug", window: window, output: output)
        var screens: [String] = []
        for (theme, appearance) in [("light", NSAppearance.Name.aqua), ("dark", NSAppearance.Name.darkAqua)] {
            NSApplication.shared.appearance = NSAppearance(named: appearance)
            window.appearance = nil
            try await pause()
            try require(model.page == .home, "Home page did not render")
            try require(!(try button("connect", in: window)).isEnabled, "Unavailable VPN must not be enabled")
            try capture(theme + "-home", window: window, output: output)
            screens.append(theme + "-home")
            try press("nav-account", in: window)
            try await waitFor("Account button did not navigate") { model.page == .account }
            try capture(theme + "-account", window: window, output: output)
            screens.append(theme + "-account")
            try type("fixture", into: "username", window: window)
            try type("synthetic-fixture-only", into: "password", window: window)
            try type("123456", into: "authenticator-code", window: window)
            try await waitFor("Native text input did not update bindings") {
                model.username == "fixture" && model.password == "synthetic-fixture-only" && model.code == "123456"
            }
            try press("back", in: window)
            try await waitFor("Back did not navigate to Home") { model.page == .home }
            try require(model.password.isEmpty && model.code.isEmpty, "Back must clear sensitive fields")
            try press("nav-settings", in: window)
            try await waitFor("Settings button did not navigate") { model.page == .settings }
            try capture(theme + "-settings", window: window, output: output)
            screens.append(theme + "-settings")
            try press("back", in: window)
            try await waitFor("Settings Back did not navigate") { model.page == .home }
        }
        try require(NSApplication.shared.windows.filter { $0.isVisible }.count == 1, "Navigation created another window")
        try require(!model.blockedAccountAction, "Navigation attempted an account action")
        let report: [String: Any] = ["passed": true, "windowCount": 1, "screens": screens,
                                     "navigationViaNativeControls": true, "secretsClearedOnBack": true]
        try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted])
            .write(to: output.appendingPathComponent("results.json"))
    }
}
