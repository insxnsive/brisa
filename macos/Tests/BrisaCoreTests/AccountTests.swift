import XCTest
@testable import BrisaCore

final class AccountTests: XCTestCase {
    func testTimeoutReapsAHelperThatIgnoresTermination() async throws {
        let helper = try fixture("trap '' TERM\nexec /usr/bin/python3 -c 'import time; time.sleep(2)'")
        let client = AccountClient(helper: helper, sessionFile: fixtureSession(), timeout: 0.2)
        let started = Date()
        do { _ = try await client.checkSession(); XCTFail("expected timeout") }
        catch { XCTAssertEqual(error as? AccountError, .timedOut) }
        XCTAssertLessThan(Date().timeIntervalSince(started), 1.5, "timeout must join the owned process without waiting for its voluntary exit")
    }

    private func fixtureSession() -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        return dir.appendingPathComponent("session.enc")
    }
    private func fixture(_ script: String) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let url = dir.appendingPathComponent("helper")
        try ("#!/bin/sh\n" + script).write(to: url, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
        addTeardownBlock { try? FileManager.default.removeItem(at: dir) }
        return url
    }

    func testRepeatedRepliesAreComplete() async throws {
        let helper = try fixture("read input\nprintf '%20000s\\n' ''\nprintf '%20000s\\n' ''\necho '{\"success\":true,\"username\":\"fixture\"}'\n")
        let client = AccountClient(helper: helper, sessionFile: fixtureSession(), timeout: 2)
        for _ in 0..<100 {
            let result = try await client.signIn(username: "fixture", password: "synthetic-password")
            XCTAssertEqual(result, .signedIn("fixture"))
        }
    }

    func testSignedOutAndTunnelUnavailable() {
        let state = AccountState()
        XCTAssertFalse(state.signedIn)
        XCTAssertFalse(state.tunnelAvailable)
    }

    func testLoginUsesStdinAndSecretFreeArguments() async throws {
        let helper = try fixture("case \"$*\" in *synthetic-password*|*123456*) exit 9;; esac\nread input\ncase \"$input\" in *synthetic-password*) echo '{\"success\":true,\"username\":\"fixture\"}';; *) exit 8;; esac\n")
        let client = AccountClient(helper: helper, sessionFile: fixtureSession())
        let result = try await client.signIn(username: "fixture", password: "synthetic-password", code: "123456")
        XCTAssertEqual(result, .signedIn("fixture"))
    }

    func testSupportedEmailSuffixIsNormalizedLikeHelper() async throws {
        let helper = try fixture("case \"$*\" in *@proton.me*) exit 9;; esac\nread input\necho '{\"success\":true,\"username\":\"fixture\"}'\n")
        let result = try await AccountClient(helper: helper, sessionFile: fixtureSession())
            .signIn(username: "fixture@proton.me", password: "synthetic-password")
        XCTAssertEqual(result, .signedIn("fixture"))
    }

    func testMissingHelperAndInvalidReply() async throws {
        let missing = AccountClient(helper: URL(fileURLWithPath: "/no/such/helper"), sessionFile: fixtureSession())
        do { _ = try await missing.checkSession(); XCTFail() } catch let error as AccountError { XCTAssertEqual(error, .missingHelper) }
        let helper = try fixture("echo 'secret garbage'\n")
        do { _ = try await AccountClient(helper: helper, sessionFile: fixtureSession()).checkSession(); XCTFail() }
        catch let error as AccountError { XCTAssertEqual(error, .invalidReply) }
    }

    func testChallengeAndTwoFactor() async throws {
        let helper = try fixture("echo '{\"success\":false,\"code\":\"TWO_FACTOR_REQUIRED\",\"error\":\"secret\"}'\nexit 1\n")
        do { _ = try await AccountClient(helper: helper, sessionFile: fixtureSession()).signIn(username: "fixture", password: "synthetic-password"); XCTFail() }
        catch let error as AccountError { XCTAssertEqual(error, .twoFactorRequired) }
        let challenge = try fixture("echo '{\"success\":false,\"code\":\"CAPTCHA_REQUIRED\",\"captchaUrl\":\"https://evil.test/\"}'\nexit 1\n")
        do { _ = try await AccountClient(helper: challenge, sessionFile: fixtureSession()).signIn(username: "fixture", password: "synthetic-password"); XCTFail() }
        catch let error as AccountError { XCTAssertEqual(error, .verificationUnsupported) }
    }

    func testCheckSessionDistinguishesTemporaryFailureAndReadsSavedIdentity() async throws {
        let temporary = try fixture("echo '{\"success\":false,\"valid\":false,\"code\":\"NETWORK_ERROR\"}'\n")
        do { _ = try await AccountClient(helper: temporary, sessionFile: fixtureSession()).checkSession(); XCTFail() }
        catch let error as AccountError { XCTAssertEqual(error, .temporaryFailure) }
        let storage = try fixture("echo '{\"success\":false,\"valid\":false,\"code\":\"SESSION_PERSISTENCE\"}'\n")
        do { _ = try await AccountClient(helper: storage, sessionFile: fixtureSession()).checkSession(); XCTFail() }
        catch let error as AccountError { XCTAssertEqual(error, .storageFailure) }
        let valid = try fixture("case \"$*\" in *-session-username*) echo '{\"success\":true,\"username\":\"fixture\"}';; *) echo 'Using saved session'; echo '{\"success\":true,\"valid\":true}';; esac\n")
        let result = try await AccountClient(helper: valid, sessionFile: fixtureSession()).checkSession()
        XCTAssertEqual(result, .signedIn("fixture"))
    }

    func testSignOutDeletesOnlyExplicitFile() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let own = dir.appendingPathComponent("session.enc"), other = dir.appendingPathComponent("other.enc")
        try Data("fixture".utf8).write(to: own)
        try Data("fixture".utf8).write(to: other)
        try AccountClient(helper: dir.appendingPathComponent("missing"), sessionFile: own).signOut()
        XCTAssertFalse(FileManager.default.fileExists(atPath: own.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: other.path))
    }

    func testTimeoutAndCancellation() async throws {
        let helper = try fixture("while :; do :; done\n")
        let client = AccountClient(helper: helper, sessionFile: fixtureSession(), timeout: 0.1)
        do { _ = try await client.checkSession(); XCTFail() } catch let error as AccountError { XCTAssertEqual(error, .timedOut) }
        let slow = AccountClient(helper: helper, sessionFile: fixtureSession(), timeout: 20)
        let task = Task { try await slow.checkSession() }
        task.cancel()
        do { _ = try await task.value; XCTFail() } catch { XCTAssertTrue(error is CancellationError || error as? AccountError == .cancelled) }
    }
}
