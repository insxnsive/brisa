import Foundation
import Darwin

/// One owner drains both pipes in order. Never race readabilityHandler against
/// readDataToEndOfFile, and never wait for EOF from an inherited descriptor.
final class HelperOutputReader: @unchecked Sendable {
    private let stdout: FileHandle
    private let stderr: FileHandle
    private let finished = DispatchGroup()
    private var bytes = Data()
    private var diagnostics = Data()
    private var invalid = false

    init(stdout: FileHandle, stderr: FileHandle) throws {
        self.stdout = stdout
        self.stderr = stderr
        for handle in [stdout, stderr] {
            let flags = fcntl(handle.fileDescriptor, F_GETFL)
            guard flags >= 0, fcntl(handle.fileDescriptor, F_SETFL, flags | O_NONBLOCK) >= 0 else {
                throw AccountError.helperFailure
            }
        }
    }

    private func drain(_ fd: Int32, into data: inout Data, scratch: inout [UInt8]) {
        // Limit each turn so a noisy stream cannot starve cancellation or stderr.
        for _ in 0..<8 {
            let count = scratch.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress, $0.count) }
            if count > 0 {
                if data.count + count > 64 * 1024 { invalid = true }
                else { data.append(contentsOf: scratch.prefix(count)) }
            } else if count == 0 || errno == EAGAIN || errno == EWOULDBLOCK {
                return
            } else if errno != EINTR {
                invalid = true
                return
            }
        }
    }

    func start(process: Process) {
        finished.enter()
        DispatchQueue.global().async {
            defer { self.finished.leave() }
            var scratch = [UInt8](repeating: 0, count: 8192)
            var stopping = false
            while process.isRunning {
                self.drain(self.stdout.fileDescriptor, into: &self.bytes, scratch: &scratch)
                self.drain(self.stderr.fileDescriptor, into: &self.diagnostics, scratch: &scratch)
                if self.invalid && !stopping { stopping = true; stopOwnedHelper(process) }
                Thread.sleep(forTimeInterval: 0.005)
            }
            self.drain(self.stdout.fileDescriptor, into: &self.bytes, scratch: &scratch)
            self.drain(self.stderr.fileDescriptor, into: &self.diagnostics, scratch: &scratch)
        }
    }

    func result() async -> Data? {
        await withCheckedContinuation { continuation in
            DispatchQueue.global().async {
                self.finished.wait()
                continuation.resume(returning: self.invalid ? nil : self.bytes)
            }
        }
    }

    // Caller joins result() after process exit before closing either descriptor.
    func close() {
        try? stdout.close()
        try? stderr.close()
    }
}
