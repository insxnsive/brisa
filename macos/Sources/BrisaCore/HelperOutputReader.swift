import Foundation
import Darwin

/// One owner pumps all three pipes, and joins before request completion.
/// Never block on stdin capacity or race two readers on the same output pipe.
final class HelperOutputReader: @unchecked Sendable {
    private let stdout: FileHandle
    private let stderr: FileHandle
    private let stdin: FileHandle
    private let finished = DispatchGroup()
    private var bytes = Data()
    private var diagnostics = Data()
    private var invalid = false

    init(stdout: FileHandle, stderr: FileHandle, stdin: FileHandle) throws {
        self.stdout = stdout
        self.stderr = stderr
        self.stdin = stdin
        for handle in [stdout, stderr, stdin] {
            let flags = fcntl(handle.fileDescriptor, F_GETFL)
            guard flags >= 0, fcntl(handle.fileDescriptor, F_SETFL, flags | O_NONBLOCK) >= 0 else {
                throw AccountError.helperFailure
            }
        }
        // A child may exit between isRunning and write(). Suppress SIGPIPE only
        // on this owned descriptor, never process-wide.
        guard fcntl(stdin.fileDescriptor, F_SETNOSIGPIPE, 1) >= 0 else {
            throw AccountError.helperFailure
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

    func start(process: Process, input: Data?) {
        finished.enter()
        DispatchQueue.global().async {
            var request = input ?? Data()
            var sent = 0
            var inputOpen = true
            func closeInput() {
                if inputOpen { try? self.stdin.close(); inputOpen = false }
                request.removeAll(keepingCapacity: false)
            }
            defer { closeInput(); self.finished.leave() }
            var scratch = [UInt8](repeating: 0, count: 8192)
            var stopping = false
            while process.isRunning {
                if inputOpen {
                    if sent < request.count {
                        let count = request.withUnsafeBytes { buffer in
                            Darwin.write(self.stdin.fileDescriptor, buffer.baseAddress!.advanced(by: sent), min(8192, buffer.count - sent))
                        }
                        if count > 0 { sent += count }
                        else if count < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR {
                            // Early helper errors may legitimately close stdin.
                            if errno != EPIPE { self.invalid = true }
                            closeInput()
                        }
                    }
                    if inputOpen && sent == request.count { closeInput() }
                }
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

    // Caller joins result() after process exit before closing the descriptors.
    func close() {
        try? stdin.close()
        try? stdout.close()
        try? stderr.close()
    }
}
