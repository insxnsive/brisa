# Brisa for macOS: development milestone

This is an experimental native SwiftUI app for macOS 13 or newer, built separately for Apple Silicon and Intel Macs. Home, Account and Settings stay in one window. Account requests use the bundled Go helper at `Brisa.app/Contents/Helpers/protonvpn-wg`.

**VPN tunneling is unavailable.** The Connect button is disabled. The app never claims that traffic is protected. There is no tunnel engine, driver, privileged install, or background network service in this milestone.

The app starts signed out and makes no network request on launch. Sign in and Check saved session are explicit actions. Passwords and authenticator codes are sent to the helper through a private stdin JSON envelope; they are not put in process arguments, environment variables, settings, or logs. A Proton verification challenge that needs a browser is currently unsupported. The app does not open challenge URLs. Sign out removes only the app's explicit local session file under `~/Library/Application Support/Brisa/`.

The helper's Darwin session encryption must be provided by its Keychain-backed implementation before real account use. There is no plaintext fallback permitted. CI uses disposable fixtures and never signs into a real account.

## Build and test on a Mac

Run `swift test` and `swift build -c release` in `macos/`, then run `python3 macos/scripts/package.py` from the repository. The script builds the Go helper natively with CGO enabled, verifies both binary architectures, and creates an ad-hoc-signed `.app`, ZIPs, and SHA-256 checksums under `artifacts/macos/<architecture>/`. The source ZIP contains the Mac source, helper source, workflow, this document, and GPL license. It excludes build output and private user state.

CI runs the Swift and Go tests, packages on native arm64 and x86_64 runners, and launches the packaged app in an explicit no-network smoke mode. CI artifacts are development builds. They have no Developer ID signature or notarization and are not releases. No automatic updater or Windows release feed is used.

The focused XCTest expectations for signed-out state, secret transport, helper failures, challenges, cancellation, timeout, session identity and local sign-out were written before the Swift implementation. On the Windows development host, `swift test` cannot run because Swift is not installed; RED/GREEN execution and packaged app behavior require the native CI jobs.

Account UI and helper protocol require Mac-native acceptance with fixture-only process tests. Real sign-in, two-factor and human verification remain unverified. Tunneling additionally needs an accepted engine, privilege model, DNS/IPv6/UDP behavior, and tunnel ownership rules before Connect can be enabled.
