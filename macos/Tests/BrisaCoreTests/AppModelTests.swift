import XCTest
import BrisaCore
@testable import Brisa

@MainActor
private final class ControlledAccount: AccountService {
    private var pending: CheckedContinuation<AccountResult, Never>?
    private var startWaiter: CheckedContinuation<Void, Never>?
    private var cancelWaiter: CheckedContinuation<Void, Never>?
    private var started = false
    private var cancelled = false
    var removedSession = false

    func signIn(username: String, password: String, code: String) async throws -> AccountResult {
        await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                pending = continuation
                started = true
                startWaiter?.resume(); startWaiter = nil
            }
        } onCancel: {
            Task { @MainActor in
                self.cancelled = true
                self.cancelWaiter?.resume(); self.cancelWaiter = nil
            }
        }
    }
    func waitForStart() async {
        if !started { await withCheckedContinuation { startWaiter = $0 } }
    }
    func waitForCancellation() async {
        if !cancelled { await withCheckedContinuation { cancelWaiter = $0 } }
    }
    func finish() { pending?.resume(returning: .signedIn("fixture")); pending = nil }
    func checkSession() async throws -> AccountResult { .signedOut }
    func signOut() throws { removedSession = true }
}

final class AppModelTests: XCTestCase {
    @MainActor
    func testBackClearsSecretsAndJoinsCancelledRequestBeforeReusingSession() async throws {
        let account = ControlledAccount()
        let model = AppModel(client: account)
        model.page = .account
        model.username = "fixture"
        model.password = "synthetic-fixture-only"
        model.code = "123456"
        model.signIn()
        await account.waitForStart()
        let back = Task { await model.back() }
        await account.waitForCancellation()
        // The cancelled service deliberately remains alive until finish().
        XCTAssertTrue(model.busy, "Keep new requests/sign-out blocked until the owned request exits")
        XCTAssertEqual(model.password, "")
        XCTAssertEqual(model.code, "")
        account.finish()
        await back.value
        XCTAssertFalse(model.busy)
        XCTAssertFalse(model.signedIn, "A cancelled successful reply must never publish account state")
        XCTAssertEqual(model.page, .home)
        await model.signOut()
        XCTAssertTrue(account.removedSession)
    }
}
