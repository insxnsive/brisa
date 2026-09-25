# macOS Foundation Implementation Plan

> **For Hermes:** Use subagent-driven-development principles at integration boundaries, with one bounded builder and parent review.

**Goal:** Start an isolated native Mac port with a buildable, tested account client and an explicit unavailable tunnel state; publish no app release.

**Architecture:** SwiftUI handles Home, Account and Settings in one compact window. A bounded async process bridge invokes the existing Go Proton helper directly, without the Windows Node/WireSock coordinator. Darwin sessions use an OS Keychain-held encryption key and authenticated encrypted disk storage, never the non-Windows plaintext fallback. Network routing is deferred until engine, privilege and ownership semantics are qualified.

**Tech Stack:** Swift Package Manager, SwiftUI/AppKit, Go, Security.framework, Python packaging, GitHub macOS CI.

## Task 1: Release discipline (parent, already on brisa)
- Files: `packaging/release.py`, `tests/packaging/test_release.py`, `docs/releasing.md`, `AGENTS.md`.
- RED: test actual mocked publish payload against a multi-version changelog; reproduce full-history body.
- GREEN: select one exact nonempty unique version section and keep notes outside the asset directory.
- Verify: `python -m unittest discover -s tests/packaging -v`; no version bump/tag/release.

## Task 2: Native account milestone (builder)
- Create: `macos/Package.swift`, `macos/Sources/BrisaCore/`, `macos/Sources/Brisa/`, `macos/Tests/BrisaCoreTests/`, `macos/scripts/`, `.github/workflows/macos.yml`, `docs/macos.md`.
- Write focused tests first for account results, process failure/cancellation and secret transport; exercise one path before expanding.
- Implement helper invocations from actual flags/protocol. Passwords and codes go only through stdin; do not copy arbitrary errors or tokens into UI/logs.
- Native macOS 13+ system controls, typography, light/dark and same-window Back navigation, no Electron/webview or landing-page design. No fake connected state.
- Bundle a matching-architecture Go helper into a runnable .app and produce ad-hoc-signed development ZIPs plus matching source/licenses. This is not trusted Developer ID signing/notarization.
- Add read-only artifact-only macOS CI, Swift tests and packaged no-network startup smoke. No releases/tags/feed or privileged networking.

## Task 3: Darwin secret storage (parent)
- Files: `tools/proton-confgen/internal/auth/session_crypto_*`, narrowly scoped session envelope code/tests.
- RED: fail fixture-only encryption roundtrip, tamper/wrong-key, malformed envelope and fail-closed target-platform tests.
- GREEN: AES-GCM with random nonces, key held using Security.framework. Darwin without native support fails closed. Keep Windows DPAPI untouched.
- Verify Go tests on Windows and target macOS CI. Any Keychain integration writes use unique disposable CI-only items/keychain, never user's login items.

## Task 4: Integration and evidence (parent)
- Review helper flags, async lifetime, cancellation/navigation/exit, packaging inventory, platform isolation and secrets.
- Run Windows offline regression, Mac-native builds/tests/package smoke on CI for arm64/x86_64; inspect/download exact artifacts and read results.
- Correct existing release bodies to their version-only notes, verify readback and unchanged assets/tags.
- Record exact verified scope and remaining gates: real sign-in/2FA/CAPTCHA, friend UI acceptance, engine/process isolation, privileged helper, DNS/IPv6/UDP, disconnect/exit ownership, Developer ID/notarization.
- Commit/push `macos`, not merge into `brisa`; no new version or GitHub Release.
