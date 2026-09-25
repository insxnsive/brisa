import SwiftUI

/// A transparent native anchor for unambiguous control discovery in UI acceptance.
private final class ControlAnchorView: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

private struct ControlAnchor: NSViewRepresentable {
    let id: String
    func makeNSView(context: Context) -> NSView {
        let view = ControlAnchorView()
        view.identifier = NSUserInterfaceItemIdentifier(id)
        view.setAccessibilityElement(false)
        return view
    }
    func updateNSView(_ view: NSView, context: Context) {
        view.identifier = NSUserInterfaceItemIdentifier(id)
    }
}

extension View {
    func nativeControlIdentifier(_ id: String) -> some View {
        accessibilityIdentifier(id).background(ControlAnchor(id: id))
    }
}
