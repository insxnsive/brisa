import Foundation
import Darwin

func stopOwnedHelper(_ process: Process) {
    guard process.isRunning else { return }
    let pid = process.processIdentifier
    guard pid > 1 else { return }
    process.terminate()
    DispatchQueue.global().asyncAfter(deadline: .now() + 0.2) {
        if process.isRunning && process.processIdentifier == pid { _ = Darwin.kill(pid, SIGKILL) }
    }
}

public enum AccountError: Error, Equatable, LocalizedError {
    case missingHelper, invalidReply, timedOut, cancelled, invalidInput, invalidCredentials
    case twoFactorRequired, twoFactorInvalid, verificationUnsupported, sessionUnavailable
    case temporaryFailure, storageFailure, helperFailure

    public var errorDescription: String? {
        switch self {
        case .missingHelper: return "The bundled account helper is missing. Reinstall this development build."
        case .invalidReply: return "The account helper returned an invalid response."
        case .timedOut: return "The account request timed out."
        case .cancelled: return "The account request was cancelled."
        case .invalidInput: return "Check the account information and try again."
        case .invalidCredentials: return "The account credentials were rejected."
        case .twoFactorRequired: return "Enter your authenticator code to continue."
        case .twoFactorInvalid: return "The authenticator code was rejected."
        case .verificationUnsupported: return "This verification challenge is not supported in this build. Cancel and try again later."
        case .sessionUnavailable: return "No valid saved session was found."
        case .temporaryFailure: return "The account could not be checked right now."
        case .storageFailure: return "The protected session could not be stored or removed."
        case .helperFailure: return "The account request failed."
        }
    }
}

public struct AccountState {
    public var signedIn = false
    public let tunnelAvailable = false
    public init() {}
}

public enum AccountResult: Equatable {
    case signedIn(String)
    case signedOut
}

private struct Reply: Decodable {
    let success: Bool
    let valid: Bool?
    let username: String?
    let code: String?
}

public struct AccountClient {
    private let helper: URL
    private let sessionFile: URL
    private let timeout: TimeInterval

    // Only tests inject a helper. Production always uses the bundle's Contents/Helpers path.
    public init(helper: URL, sessionFile: URL, timeout: TimeInterval = 30) {
        self.helper = helper
        self.sessionFile = sessionFile
        self.timeout = timeout
    }

    public static func bundled() throws -> AccountClient {
        guard let executable = Bundle.main.executableURL else { throw AccountError.missingHelper }
        let contents = executable.deletingLastPathComponent().deletingLastPathComponent()
        let helper = contents.appendingPathComponent("Helpers/protonvpn-wg")
        let support = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Brisa", isDirectory: true)
        return AccountClient(helper: helper, sessionFile: support.appendingPathComponent("session.enc"))
    }

    private func prepareDirectory() throws {
        let dir = sessionFile.deletingLastPathComponent()
        if (try? dir.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
            throw AccountError.storageFailure
        }
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        let kind = try dir.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard kind.isDirectory == true, kind.isSymbolicLink != true else { throw AccountError.storageFailure }
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: dir.path)
    }

    public func signOut() throws {
        // The explicit app-owned file only; no legacy path discovery or directory scans.
        if FileManager.default.fileExists(atPath: sessionFile.path) {
            do { try FileManager.default.removeItem(at: sessionFile) }
            catch { throw AccountError.storageFailure }
        }
    }

    public func signIn(username: String, password: String, code: String = "") async throws -> AccountResult {
        var name = username.trimmingCharacters(in: .whitespacesAndNewlines)
        for suffix in ["@protonmail.com", "@proton.me", "@pm.me"] where name.hasSuffix(suffix) {
            name.removeLast(suffix.count)
            break
        }
        guard !name.isEmpty, name.count <= 320, !name.hasPrefix("-"),
              !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }),
              !password.isEmpty, password.utf8.count <= 16_384 else { throw AccountError.invalidInput }
        guard code.isEmpty || (code.utf8.count == 6 && code.utf8.allSatisfy({ $0 >= 48 && $0 <= 57 })) else { throw AccountError.invalidInput }
        let secrets = ["password": password, "twoFactorCode": code]
        let input = try JSONSerialization.data(withJSONObject: secrets)
        let reply = try await invoke(["-login-only", "-json", "-stdin-secrets", "-username", name], input: input)
        guard reply.success, let returned = reply.username, returned == name else { throw AccountError.invalidReply }
        return .signedIn(returned)
    }

    public func checkSession() async throws -> AccountResult {
        let reply = try await invoke(["-check-session", "-json"], input: nil)
        if reply.success && reply.valid == true {
            let identity = try await invoke(["-session-username", "-json"], input: nil)
            guard identity.success, let name = identity.username, !name.isEmpty, name.count <= 320,
                  !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
                throw AccountError.invalidReply
            }
            return .signedIn(name)
        }
        if !reply.success && reply.valid == false {
            if reply.code == "NETWORK_ERROR" { throw AccountError.temporaryFailure }
            if reply.code == "SESSION_PERSISTENCE" { throw AccountError.storageFailure }
            if reply.code == "INVALID_SESSION" { throw AccountError.sessionUnavailable }
            throw AccountError.helperFailure
        }
        throw AccountError.invalidReply
    }

    private func invoke(_ args: [String], input: Data?) async throws -> Reply {
        guard !Task.isCancelled else { throw AccountError.cancelled }
        guard FileManager.default.isExecutableFile(atPath: helper.path) else { throw AccountError.missingHelper }
        do { try prepareDirectory() } catch { throw AccountError.storageFailure }
        let process = Process()
        let exit = HelperExit()
        process.terminationHandler = { _ in exit.signal() }
        process.executableURL = helper
        process.arguments = args + ["-session-file", sessionFile.path]
        // Inherit only a minimal environment; no app supplied secret or helper override.
        process.environment = ["PATH": "/usr/bin:/bin", "HOME": FileManager.default.homeDirectoryForCurrentUser.path]
        let stdout = Pipe(), stderr = Pipe(), stdin = Pipe()
        process.standardOutput = stdout
        process.standardError = stderr
        process.standardInput = stdin
        let output = try HelperOutputReader(stdout: stdout.fileHandleForReading, stderr: stderr.fileHandleForReading)
        defer { output.close() }
        do { try process.run() } catch { throw AccountError.missingHelper }
        output.start(process: process)
        // JSON is bounded above before process launch; write on a queue so cancellation remains responsive.
        DispatchQueue.global().async {
            if let input { try? stdin.fileHandleForWriting.write(contentsOf: input) }
            try? stdin.fileHandleForWriting.close()
        }
        let limit = timeout
        let timedOut: Bool = await withTaskCancellationHandler {
            await withTaskGroup(of: Bool.self) { group in
                group.addTask {
                    await exit.wait()
                    return false
                }
                group.addTask {
                    try? await Task.sleep(nanoseconds: UInt64(max(0.01, limit) * 1_000_000_000))
                    stopOwnedHelper(process)
                    return true
                }
                let first = await group.next() ?? true
                group.cancelAll()
                return first
            }
        } onCancel: {
            stopOwnedHelper(process)
        }
        let captured = await output.result()
        if Task.isCancelled { throw AccountError.cancelled }
        if timedOut { throw AccountError.timedOut }
        // The existing helper may print status lines before its final JSON record.
        // Accept only a bounded, complete final JSON line and never display preceding output.
        guard let data = captured, data.count <= 64 * 1024,
              let text = String(data: data, encoding: .utf8),
              let last = text.split(whereSeparator: \.isNewline).last,
              let reply = try? JSONDecoder().decode(Reply.self, from: Data(last.utf8)) else {
            throw AccountError.invalidReply
        }
        if process.terminationStatus == 0 { return reply }
        switch reply.code {
        case "TWO_FACTOR_REQUIRED": throw AccountError.twoFactorRequired
        case "TWO_FACTOR_INVALID": throw AccountError.twoFactorInvalid
        case "INVALID_CREDENTIALS": throw AccountError.invalidCredentials
        case "CAPTCHA_REQUIRED", "CAPTCHA_INVALID", "HUMAN_VERIFICATION_UNSUPPORTED": throw AccountError.verificationUnsupported
        case "SESSION_PERSISTENCE": throw AccountError.storageFailure
        case "NETWORK_ERROR": throw AccountError.temporaryFailure
        default: throw AccountError.helperFailure
        }
    }
}
