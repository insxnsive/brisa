# Brisa

Brisa is a Windows x64 .NET 10 WPF fork of GoLiveBypass. Only the native app ships.

- Write user-facing docs in plain English. Keep the approved Fluent UI and same-window navigation.
- `src/Brisa` owns the UI; `backend` owns the bundled Node coordinator; `tools/proton-confgen` owns the Go helper.
- Preserve GPL notices and upstream authorship. Do not restore the removed Electron app, plugin, Linux/macOS clients or website.
- Never read real accounts, sessions, WireGuard configs or credentials. Tests use isolated fixtures.
- Do not sign in, connect, stop a tunnel or install network drivers while testing. Another app's WireSock tunnel must remain untouched.
- Automatic updates use versioned Brisa releases, never upstream GoLiveBypass releases or arbitrary commits. Never embed a GitHub token.
- Run relevant tests before committing. WPF builds are not UI acceptance; also exercise the packaged executable in isolated mode.
- Use test-first fixes. Keep workers in their assigned paths; no worker commits or publishes.
- Release target: `insxnsive/brisa`, public with user authorization. Publication requires verified artifacts and matching source.
