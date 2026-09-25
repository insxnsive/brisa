# Brisa for macOS: development milestone

This branch develops a native SwiftUI app for macOS 13 or newer, with separate Apple Silicon (`arm64`) and Intel (`x86_64`) builds. Home, Account and Settings share one window. Windows development and distribution remain on `brisa`.

**This is not a working Mac VPN yet.** Connect is disabled and the app does not claim to protect traffic. This milestone has no tunnel engine, privileged installation, driver, DNS changes or background network service.

## Account foundation

The app starts signed out and makes no network request on launch. Sign In and Check Saved Session are explicit actions. Account requests invoke the bundled Go helper at `Brisa.app/Contents/Helpers/protonvpn-wg` directly.

- Passwords and authenticator codes travel through private stdin JSON, not process arguments, environment variables or logs.
- Helper errors map to fixed messages. Arbitrary diagnostic text and verification URLs are not displayed or opened.
- Browser-based Proton verification challenges are not supported yet.
- Sessions use AES-256-GCM with random nonces and a key in the local macOS login Keychain. The encrypted file lives under `~/Library/Application Support/Brisa/`, with private directory/file permissions.
- Missing, inaccessible or malformed keys fail closed. Darwin builds without CGO/native Keychain support fail closed. There is no plaintext fallback or automatic import of Windows/legacy session files.
- The development helper explicitly uses the local login Keychain, not the entitlement-gated Data Protection Keychain. Keychain access remains subject to the user's lock/access-control settings. Stable Developer ID signing and behavior across app upgrades still need qualification; an ad-hoc rebuild may require renewed Keychain authorization.
- Sign Out removes the explicit local session file, not other applications' state. It is not a server-side token-revocation feature.

## Build on a Mac

Install Xcode Command Line Tools, Go matching `tools/proton-confgen/go.mod`, and Python 3. From this branch:

```sh
cd macos
swift test
swift build -c release
cd ..
python3 -m unittest discover -s macos/Tests -v
(cd tools/proton-confgen && go test ./...)
python3 macos/scripts/package.py
python3 macos/scripts/ui_acceptance.py "artifacts/macos/$(uname -m)/Brisa.app"
```

Packaging builds the Go helper natively with CGO, vendors its dependency source, checks both binary architectures and ad-hoc-signs the app. Output under `artifacts/macos/<architecture>/` includes the app ZIP, matching source ZIP and SHA-256 checksums. The source archive includes helper dependencies and their license files, build recipes, native code and the project GPL license. Private user state and build caches are excluded.

Development ZIPs are **not Developer ID signed or notarized**, and ad-hoc signing is not publisher verification. Gatekeeper may block downloaded builds. Do not disable Gatekeeper globally. Review the source or use macOS's explicit approval for a build you trust; distribution acceptance remains outstanding.

## Verification boundaries

The Mac workflow runs on native Apple Silicon and Intel runners. It tests the account subprocess protocol, timeout/cancellation, secret transport and disposable Keychain encryption, then builds and packages the real app. Native UI acceptance launches the packaged executable directly and through Launch Services, renders light/dark Home, Account and Settings, and uses real AppKit buttons and field editors for navigation and text input. It must produce screenshots and a passing JSON report; an early process exit is not startup acceptance.

CI never signs into a real account or changes networking. Keychain tests use randomly named disposable items and synthetic session data. Development artifacts do not publish a GitHub Release, touch the Windows updater feed or bump an application release version.

Real Proton sign-in, two-factor/browser challenges, friend-run Mac acceptance, upgrade behavior and trusted signing remain unverified. The next networking milestone needs an accepted engine, privilege model, per-process isolation, DNS/IPv6/UDP behavior and disconnect/exit ownership before Connect can be enabled.
