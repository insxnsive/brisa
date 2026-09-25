import AppKit
import Foundation

/// CI-only in-process accessibility acceptance of the actual rendered SwiftUI window.
/// It never submits credentials or invokes the account/network helper.
@MainActor
enum NativeSmoke {
    private static var started = false
    private enum Failure: Error { case assertion(String) }

    static func start(model: AppModel) {
        guard !started, let index = CommandLine.arguments.firstIndex(of: "--smoke-no-network") else { return }
        started = true
        guard CommandLine.arguments.indices.contains(index + 1) else { exit(64) }
        let output = URL(fileURLWithPath: CommandLine.arguments[index + 1], isDirectory: true)
        // A dead event loop must never turn into an unbounded CI hang.
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
    private static func elements(_ root: Any) -> [any NSAccessibilityProtocol] {
        var seen = Set<ObjectIdentifier>()
        func visit(_ value: Any) -> [any NSAccessibilityProtocol] {
            guard let item = value as? any NSAccessibilityProtocol,
                  seen.insert(ObjectIdentifier(item as AnyObject)).inserted else { return [] }
            guard seen.count < 2000 else { return [] }
            return [item] + (item.accessibilityChildren() ?? []).flatMap(visit)
        }
        return visit(root)
    }
    private static func element(_ id: String, in window: NSWindow) throws -> any NSAccessibilityProtocol {
        if let found = elements(window).first(where: { $0.accessibilityIdentifier() == id }) { return found }
        let identifiers = elements(window).compactMap { $0.accessibilityIdentifier() }
        throw Failure.assertion("Missing native control \(id); found \(identifiers)")
    }
    private static func press(_ id: String, in window: NSWindow) throws {
        try require(try element(id, in: window).accessibilityPerformPress(), "Native button did not activate: \(id)")
    }
    private static func capture(_ name: String, window: NSWindow, output: URL) throws {
        guard let view = window.contentView else { throw Failure.assertion("Missing window content") }
        view.layoutSubtreeIfNeeded()
        view.displayIfNeeded()
        guard let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { throw Failure.assertion("No rendered bitmap") }
        view.cacheDisplay(in: view.bounds, to: bitmap)
        guard let data = bitmap.representation(using: .png, properties: [:]) else { throw Failure.assertion("PNG encoding failed") }
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
        var screens: [String] = []
        for (theme, appearance) in [("light", NSAppearance.Name.aqua), ("dark", NSAppearance.Name.darkAqua)] {
            window.appearance = NSAppearance(named: appearance)
            try await pause()
            try require(model.page == .home, "Home page did not render")
            try require(!(try element("connect", in: window)).isAccessibilityEnabled(), "Unavailable VPN must not be enabled")
            try capture(theme + "-home", window: window, output: output)
            screens.append(theme + "-home")
            try press("nav-account", in: window)
            try await pause()
            try require(model.page == .account, "Account button did not navigate")
            try capture(theme + "-account", window: window, output: output)
            screens.append(theme + "-account")
            try element("username", in: window).setAccessibilityValue("fixture")
            try element("password", in: window).setAccessibilityValue("synthetic-fixture-only")
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
                                     "navigationViaAccessibility": true, "secretsClearedOnBack": true]
        try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted])
            .write(to: output.appendingPathComponent("results.json"))
    }
}
