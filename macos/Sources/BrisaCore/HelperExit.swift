import Foundation

/// Remember exits that arrive before the asynchronous waiter is installed.
/// Register signal() as Process.terminationHandler before launching the child.
final class HelperExit: @unchecked Sendable {
    private let lock = NSLock()
    private var exited = false
    private var waiter: CheckedContinuation<Void, Never>?

    func signal() {
        lock.lock()
        exited = true
        let pending = waiter
        waiter = nil
        lock.unlock()
        pending?.resume()
    }

    func wait() async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if exited {
                lock.unlock()
                continuation.resume()
            } else {
                waiter = continuation
                lock.unlock()
            }
        }
    }
}
