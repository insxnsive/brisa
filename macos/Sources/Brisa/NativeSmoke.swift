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
    private struct Element {
        let object: NSObject
        func value(_ name: String) -> Any? {
            let selector = NSSelectorFromString(name)
            guard object.responds(to: selector) else { return nil }
            return object.perform(selector)?.takeUnretainedValue()
        }
        var identifier: String? { value("accessibilityIdentifier") as? String }
        func boolean(_ name: String) throws -> Bool {
            let selector = NSSelectorFromString(name)
            guard object.responds(to: selector) else { throw Failure.assertion("Missing native method \(name)") }
            typealias Function = @convention(c) (AnyObject, Selector) -> Bool
            return unsafeBitCast(object.method(for: selector), to: Function.self)(object, selector)
        }
        func setValue(_ text: String) throws {
            let selector = NSSelectorFromString("setAccessibilityValue:")
            guard object.responds(to: selector) else { throw Failure.assertion("Native field is not editable") }
            typealias Function = @convention(c) (AnyObject, Selector, AnyObject) -> Void
            unsafeBitCast(object.method(for: selector), to: Function.self)(object, selector, text as NSString)
        }
    }
    private static func elements(_ root: Any) -> [Element] {
        var seen = Set<ObjectIdentifier>()
        func visit(_ value: Any) -> [Element] {
            guard let object = value as? NSObject,
                  seen.insert(ObjectIdentifier(object)).inserted, seen.count < 2000 else { return [] }
            let item = Element(object: object)
            var children = item.value("accessibilityChildren") as? [Any] ?? []
            if let window = object as? NSWindow, let content = window.contentView { children.append(content) }
            if let view = object as? NSView { children.append(contentsOf: view.subviews) }
            return [item] + children.flatMap(visit)
        }
        return visit(root)
    }
    private static func element(_ id: String, in window: NSWindow) throws -> Element {
        if let found = elements(window).first(where: { $0.identifier == id }) { return found }
        let identifiers = elements(window).prefix(80).map { item in
            "\(type(of: item.object)): id=\(item.identifier ?? "-") role=\(item.value("accessibilityRole") ?? "-") title=\(item.value("title") ?? "-") label=\(item.value("accessibilityLabel") ?? "-")"
        }
        throw Failure.assertion("Missing native control \(id); found \(identifiers)")
    }
    private static func press(_ id: String, in window: NSWindow) throws {
        try require(try element(id, in: window).boolean("accessibilityPerformPress"), "Native button did not activate: \(id)")
    }
    private static func capture(_ name: String, window: NSWindow, output: URL) throws {
        guard let view = window.contentView else { throw Failure.assertion("Missing window content") }
        view.layoutSubtreeIfNeeded()
        view.displayIfNeeded()
        guard let image = CGWindowListCreateImage(.null, .optionIncludingWindow,
                                                  CGWindowID(window.windowNumber), [.boundsIgnoreFraming, .bestResolution]) else {
            throw Failure.assertion("No image for the owned application window")
        }
        let bitmap = NSBitmapImageRep(cgImage: image)
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
        window.makeKeyAndOrderFront(nil)
        NSApplication.shared.activate(ignoringOtherApps: true)
        try await pause()
        try? capture("startup-debug", window: window, output: output)
        var screens: [String] = []
        for (theme, appearance) in [("light", NSAppearance.Name.aqua), ("dark", NSAppearance.Name.darkAqua)] {
            window.appearance = NSAppearance(named: appearance)
            try await pause()
            try require(model.page == .home, "Home page did not render")
            try require(!(try element("connect", in: window).boolean("isAccessibilityEnabled")), "Unavailable VPN must not be enabled")
            try capture(theme + "-home", window: window, output: output)
            screens.append(theme + "-home")
            try press("nav-account", in: window)
            try await pause()
            try require(model.page == .account, "Account button did not navigate")
            try capture(theme + "-account", window: window, output: output)
            screens.append(theme + "-account")
            try element("username", in: window).setValue("fixture")
            try element("password", in: window).setValue("synthetic-fixture-only")
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
