# Brisa for macOS: development milestone

This branch develops a native SwiftUI app for macOS 13 or newer, with separate Apple Silicon (`arm64`) and Intel (`x86_64`) builds. Home, Account and Settings share one window. Windows development and distribution remain on `brisa`.

**This is not a working Mac VPN yet.** Connect is disabled and the app does not claim to protect traffic. A userspace WireGuard transport and an explicit loopback diagnostic are available for development, but the app has no macOS traffic interception, privileged installation, driver, system DNS changes or background network service.

## Account foundation

The app starts signed out and makes no network request on launch. Sign In and Check Saved Session are explicit actions. Account requests invoke the bundled Go helper at `Brisa.app/Contents/Helpers/protonvpn-wg` directly.

- Passwords and authenticator codes travel through private stdin JSON, not process arguments, environment variables or logs.
- One nonblocking owner pumps helper input/output; cancellation joins the owned process and I/O before a replacement request can start. Leaving Account clears password/code fields immediately and again when cleanup finishes.
- Helper errors map to fixed messages. Arbitrary diagnostic text and verification URLs are not displayed or opened.
- Browser-based Proton verification challenges are not supported yet.
- Sessions use AES-256-GCM with random nonces and a key in the local macOS login Keychain. The encrypted file lives under `~/Library/Application Support/Brisa/`, with private directory/file permissions.
- Missing, inaccessible or malformed keys fail closed. Darwin builds without CGO/native Keychain support fail closed. There is no plaintext fallback or automatic import of Windows/legacy session files.
- The development helper explicitly uses the local login Keychain, not the entitlement-gated Data Protection Keychain. Keychain access remains subject to the user's lock/access-control settings. Stable Developer ID signing and behavior across app upgrades still need qualification; an ad-hoc rebuild may require renewed Keychain authorization.
- Sign Out removes the explicit local session file, not other applications' state. It is not a server-side token-revocation feature.

## Userspace transport milestone

`tools/proton-confgen/internal/macengine` owns a WireGuard device and an isolated userspace IPv4 stack. It supports TCP and UDP, including name resolution through explicitly configured in-tunnel DNS servers. It never falls back to the host network or resolver for forwarded connections. Configuration is typed and validated; errors do not echo private keys or raw configuration. Close cancels and joins pending operations, closes owned connections, and prevents late dial results from escaping.

This first transport is **IPv4-only**. Unsupported protocols and address families are rejected. This is not a host-wide VPN, an application filter, a system DNS policy, or an IPv6 leak-prevention mechanism.

The separate `brisa-tunnel-check --self-test` executable generates fresh synthetic keys and exchanges TCP payloads, distinct UDP datagrams and an `.invalid` DNS answer between two WireGuard peers bound only to loopback. Its inner IP addresses and listeners exist inside the userspace stacks, not on the host network. It accepts no configuration file, saved session, real account or remote peer, and does not run on normal app startup. A passing JSON report is printed only after the actual exchanges and shutdown checks succeed.

The packaged-binary acceptance script requires strict, complete loopback evidence, successful exit, clean stderr and an unchanged binary hash. Failed or timed-out checks replace any old passing evidence with a fixed failure code; raw helper output is not copied into reports.

## Build on a Mac

Install Xcode Command Line Tools, Go matching `tools/proton-confgen/go.mod`, and Python 3. From this branch:

```sh
cd macos
swift test
swift build -c release
cd ..
python3 -m unittest discover -s macos/Tests -v
(cd tools/proton-confgen && go test -timeout 120s ./internal/auth ./internal/config ./cmd/protonvpn-wg)
(cd tools/proton-confgen && go test -race -timeout 90s ./internal/macengine ./cmd/brisa-tunnel-check)
python3 macos/scripts/package.py
python3 macos/scripts/transport_acceptance.py \
  --binary "artifacts/macos/$(uname -m)/Brisa.app/Contents/Helpers/brisa-tunnel-check" \
  --output "artifacts/macos/$(uname -m)/transport-evidence/results.json"
python3 macos/scripts/ui_acceptance.py "artifacts/macos/$(uname -m)/Brisa.app"
```

Packaging builds both Go executables natively with CGO, pins their compiler/linker deployment target to macOS 13, vendors dependency source, checks every executable's architecture and actual Mach-O minimum version, and ad-hoc-signs the helpers before the app. Output under `artifacts/macos/<architecture>/` includes the app ZIP, matching source ZIP and SHA-256 checksums. The source archive includes helper dependencies and their license files, build recipes, native code and the project GPL license. Private user state and build caches are excluded.

Development ZIPs are **not Developer ID signed or notarized**, and ad-hoc signing is not publisher verification. Gatekeeper may block downloaded builds. Do not disable Gatekeeper globally. Review the source or use macOS's explicit approval for a build you trust; distribution acceptance remains outstanding.

## Verification boundaries

The Mac workflow runs on native Apple Silicon and Intel runners. It tests the account subprocess protocol, timeout/cancellation, repeated large-input cancellations, secret transport and disposable Keychain encryption, then builds and packages the real app. Native UI acceptance launches the packaged executable directly and through Launch Services, renders light/dark Home, Account and Settings, and uses named control anchors, mouse events and real field editors for navigation and text input. It checks rendered surface appearance and sensitive-field clearing, then quits through Command-Q. Both a passing screenshot/JSON report and normal process exit are required; an early process exit is not startup acceptance.

The workflow also repeats the isolated transport tests with Go's race detector and executes the exact packaged diagnostic on both architectures. `transport-evidence/results.json` records its scope-limited checks and SHA-256. The evidence upload retains this report and transport test output even when another acceptance step fails.

Offline UI mode blocks account actions even if an automation target is wrong. CI never signs into a real account, creates a host tunnel or changes system routing/DNS. Transport tests use synthetic loopback peers; Keychain tests use randomly named disposable items and synthetic session data. Development artifacts do not publish a GitHub Release, touch the Windows updater feed or bump an application release version.

Real Proton sign-in, two-factor/browser challenges, friend-run Mac acceptance, macOS 13 device acceptance, upgrade behavior and trusted signing remain unverified. The current native runners are macOS 14 on Apple Silicon and macOS 15 on Intel; a checked deployment target is not hardware acceptance. Before Connect can be enabled, networking still needs a supported macOS provider/deployment path, verified source-app identity and isolation, complete DNS/IPv6 policy, real-server UDP behavior, network-change recovery and end-to-end disconnect/exit ownership. The loopback diagnostic does not establish any of those OS-level properties.

See [the transport implementation plan](plans/macos-transport-core.md) for the scope and primary Apple/WireGuard references. Apple's macOS-specific per-app manager API and broader MDM deployment guidance must be validated on a real Mac before choosing the final app-interception path; system-wide routing is not an acceptable substitute for app-specific isolation.
