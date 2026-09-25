import SwiftUI
import BrisaCore

@main
struct BrisaApp: App {
    init() { NSApplication.shared.setActivationPolicy(.regular) }

    var body: some Scene {
        Window("Brisa", id: "main") { ContentView() }
            .defaultSize(width: 440, height: 440)
    }
}

enum Page: Equatable { case home, account, settings }

@MainActor
protocol AccountService {
    func signIn(username: String, password: String, code: String) async throws -> AccountResult
    func checkSession() async throws -> AccountResult
    func signOut() throws
}

extension AccountClient: AccountService {}

@MainActor
final class AppModel: ObservableObject {
    @Published var page: Page = .home
    @Published var signedIn = AccountState().signedIn
    @Published var username = ""
    @Published var password = ""
    @Published var code = ""
    @Published var message = ""
    @Published var busy = false
    private var task: Task<Void, Never>?
    private var requestID = 0
    private let client: AccountService?

    init(client: AccountService? = try? AccountClient.bundled()) { self.client = client }

    func back() { cancel(); page = .home }
    func cancel() { requestID += 1; task?.cancel(); busy = false }
    func signIn() {
        guard !busy else { return }
        guard let client else { message = AccountError.missingHelper.localizedDescription; return }
        busy = true; message = ""
        requestID += 1
        let current = requestID
        let name = username, secret = password, factor = code
        task = Task {
            defer { if current == requestID { busy = false; password = ""; code = "" } }
            do {
                let result = try await client.signIn(username: name, password: secret, code: factor)
                guard !Task.isCancelled, current == requestID else { return }
                if case .signedIn(let account) = result { username = account; signedIn = true; message = "Signed in." }
            } catch let error as AccountError {
                if !Task.isCancelled, current == requestID, error != .cancelled { message = error.localizedDescription }
            } catch { if !Task.isCancelled, current == requestID { message = AccountError.helperFailure.localizedDescription } }
        }
    }
    func check() {
        guard !busy else { return }
        guard let client else { message = AccountError.missingHelper.localizedDescription; return }
        busy = true; message = ""
        requestID += 1
        let current = requestID
        task = Task {
            defer { if current == requestID { busy = false } }
            do {
                let result = try await client.checkSession()
                guard !Task.isCancelled, current == requestID else { return }
                if case .signedIn(let account) = result { signedIn = true; username = account; message = "Saved session is valid." }
            } catch let error as AccountError {
                if !Task.isCancelled, current == requestID {
                    if error == .sessionUnavailable { signedIn = false }
                    if error != .cancelled { message = error.localizedDescription }
                }
            } catch { if !Task.isCancelled, current == requestID { message = AccountError.helperFailure.localizedDescription } }
        }
    }
    func signOut() {
        cancel()
        do { try client?.signOut(); signedIn = false; username = ""; message = "Signed out on this Mac." }
        catch { message = AccountError.storageFailure.localizedDescription }
    }
}

private struct ContentView: View {
    @StateObject private var model = AppModel()
    private let capability = AccountState()
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack {
                if model.page != .home {
                    Button { model.back() } label: { Label("Back", systemImage: "chevron.left") }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("back")
                }
                Spacer()
                Text("Brisa").font(.headline)
                Spacer()
            }
            Divider()
            switch model.page {
            case .home: home
            case .account: account
            case .settings: settings
            }
            Spacer(minLength: 0)
        }
        .padding(24)
        .frame(minWidth: 400, minHeight: 420)
        .onAppear { NativeSmoke.start(model: model) }
        .onDisappear { model.cancel() }
    }

    private var home: some View {
        VStack(alignment: .leading, spacing: 16) {
            Label("VPN unavailable on macOS", systemImage: "lock.slash")
                .font(.title3.weight(.semibold))
            Text("This development build can manage a Proton account. It cannot create or protect a VPN connection.")
                .foregroundStyle(.secondary)
            Button("Connect") {}.disabled(!capability.tunnelAvailable).accessibilityIdentifier("connect")
            Divider()
            Button { model.page = .account } label: {
                Label("Account", systemImage: "person.crop.circle")
            }.accessibilityIdentifier("nav-account")
            Button { model.page = .settings } label: {
                Label("Settings", systemImage: "gearshape")
            }.accessibilityIdentifier("nav-settings")
        }
    }

    private var account: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Account").font(.title2.weight(.semibold))
            Text(model.signedIn ? "Signed in as \(model.username)" : "Signed out")
                .foregroundStyle(.secondary)
            if !model.signedIn {
                TextField("Username", text: $model.username)
                    .textContentType(.username).accessibilityIdentifier("username")
                SecureField("Password", text: $model.password)
                    .textContentType(.password).accessibilityIdentifier("password")
                SecureField("Authenticator code, if requested", text: $model.code)
                HStack {
                    Button("Sign In") { model.signIn() }.disabled(model.busy)
                    Button("Check Saved Session") { model.check() }.disabled(model.busy)
                }
            } else {
                Button("Sign Out on This Mac") { model.signOut() }.disabled(model.busy)
            }
            if model.busy { ProgressView().controlSize(.small) }
            if !model.message.isEmpty { Text(model.message).foregroundStyle(.secondary) }
        }
    }

    private var settings: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Settings").font(.title2.weight(.semibold))
            Label("Tunnel support is not yet available", systemImage: "info.circle")
            Text("There are no connection settings in this milestone.")
                .foregroundStyle(.secondary)
        }
    }
}
