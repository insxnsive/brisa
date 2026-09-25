# Brisa

Brisa is a native fork of GoLiveBypass. The `brisa` branch ships the Windows x64 .NET 10 WPF app. This `macos` branch develops a separate experimental SwiftUI macOS port.

- Write user-facing docs in plain English. Keep the approved Fluent UI and same-window navigation.
- `src/Brisa` owns the UI; `backend` owns the bundled Node coordinator; `tools/proton-confgen` owns the Go helper.
- Preserve GPL notices and upstream authorship. Do not restore the removed Electron app, plugin, legacy Linux/macOS clients or website. The newly authorized native macOS implementation lives in `macos/`; keep Windows UI/networking behavior intact.
- macOS development uses CI artifacts only, never Windows release feeds. Require Keychain-backed session encryption, no plaintext fallback, and honest unavailable tunnel state until a real macOS engine/privilege/ownership path passes acceptance.
- Never read real accounts, sessions, WireGuard configs or credentials. Tests use isolated fixtures.
- Do not sign in, connect, stop a tunnel or install network drivers while testing. Another app's WireSock tunnel must remain untouched.
- Automatic updates use versioned Brisa releases, never upstream GoLiveBypass releases or arbitrary commits. Never embed a GitHub token.
- Run relevant tests before committing. WPF builds are not UI acceptance; also exercise the packaged executable in isolated mode.
- Use test-first fixes. Keep workers in their assigned paths; no worker commits or publishes.
- Release target: `insxnsive/brisa`, public with user authorization. Publication requires verified artifacts and matching source.
- Development fixes stay in commits and CI artifacts. Do not bump versions, tag or publish without a maintainer-requested release. GitHub release notes contain only that version's changes; the cumulative history belongs in `CHANGELOG.md`.
