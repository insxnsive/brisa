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
                NSApplication.shared.terminate(nil)
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
    private static func require(_ condition: Bool, _ message: String) throws {
        if !condition { throw Failure.assertion(message) }
    }
    private static func views(in window: NSWindow) -> [NSView] {
        func visit(_ view: NSView) -> [NSView] { [view] + view.subviews.flatMap(visit) }
        guard let root = window.contentView else { return [] }
        return visit(root).filter { !$0.isHiddenOrHasHiddenAncestor && $0.bounds.width > 0 && $0.bounds.height > 0 }
    }
    private static func readingOrder(_ left: NSView, _ right: NSView) -> Bool {
        let a = left.convert(left.bounds, to: nil), b = right.convert(right.bounds, to: nil)
        return abs(a.midY - b.midY) < 2 ? a.minX < b.minX : a.midY > b.midY
    }
    private static func button(_ id: String, in window: NSWindow) throws -> NSButton {
        // SwiftUI exports its AX nodes lazily on headless runners. Exercise the real
        // underlying AppKit controls instead; do not invoke model actions directly.
        let controls = views(in: window).compactMap { $0 as? NSButton }.sorted(by: readingOrder)
        let indices = ["connect": 0, "nav-account": 1, "nav-settings": 2, "back": 0]
        guard let index = indices[id], controls.indices.contains(index) else {
            throw Failure.assertion("Missing native button \(id); found \(controls.count)")
        }
        return controls[index]
    }
    private static func press(_ id: String, in window: NSWindow) throws {
        let control = try button(id, in: window)
        try require(control.isEnabled, "Native button is disabled: \(id)")
        control.performClick(nil)
    }
    private static func type(_ text: String, into index: Int, window: NSWindow) throws {
        let fields = views(in: window).compactMap { $0 as? NSTextField }.filter { $0.isEditable }.sorted(by: readingOrder)
        try require(fields.indices.contains(index), "Missing native text field")
        let field = fields[index]
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
        guard let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
            throw Failure.assertion("PNG encoding failed")
        }
        try data.write(to: output.appendingPathComponent(name + ".png"))
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
            window.appearance = NSAppearance(named: appearance)
            try await pause()
            try require(model.page == .home, "Home page did not render")
            try require(!(try button("connect", in: window)).isEnabled, "Unavailable VPN must not be enabled")
            try capture(theme + "-home", window: window, output: output)
            screens.append(theme + "-home")
            try press("nav-account", in: window)
            try await pause()
            try require(model.page == .account, "Account button did not navigate")
            try capture(theme + "-account", window: window, output: output)
            screens.append(theme + "-account")
            try type("fixture", into: 0, window: window)
            try type("synthetic-fixture-only", into: 1, window: window)
            try await pause()
            try require(model.username == "fixture" && model.password == "synthetic-fixture-only", "Native text input did not update bindings")
            try press("back", in: window)
            try await pause()
            try require(model.page == .home && model.password.isEmpty && model.code.isEmpty, "Back must clear sensitive fields")
            try press("nav-settings", in: window)
            try await pause()
            try require(model.page == .settings, "Settings button did not navigate")
            try capture(theme + "-settings", window: window, output: output)
            screens.append(theme + "-settings")
            try press("back", in: window)
            try await pause()
        }
        try require(NSApplication.shared.windows.filter { $0.isVisible }.count == 1, "Navigation created another window")
        let report: [String: Any] = ["passed": true, "windowCount": 1, "screens": screens,
                                     "navigationViaNativeControls": true, "secretsClearedOnBack": true]
        try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted])
            .write(to: output.appendingPathComponent("results.json"))
    }
}
