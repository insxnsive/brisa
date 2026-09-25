# Changes

## 0.1.0-beta.4 (unreleased)

- On first connection, detect and obtain the compatible x64 WireSock SDK directly from WireSock when missing. Verify the pinned installer hash and show the vendor's installer and Windows elevation prompts; Brisa does not bundle SDK files or install silently.

## 0.1.0-beta.3

- Fix Discord shutdown falsely failing on a child that is already exiting after its parent closes; validate and terminate against a held OS process handle without weakening path ownership checks.
- Keep Brisa and the connection running in the tray when the window is closed, including installations with the old close-to-tray option disabled. Only explicit Exit or an update restart performs shutdown.
- Open inline Proton sign-in for new accounts, make the signed-out primary action Sign In, and link directly to Proton’s free-account registration page.

- Close and confirm Discord before changing its route, then reopen and confirm it after the tunnel settles. Repeat the sequence when disconnecting.
- Resolve the real versioned Discord client instead of its short-lived root launcher, and restart running clients rather than opening every installed flavor.
- Batch exact-path-checked process termination so multi-process clients do not exhaust the shutdown window. Restore Discord on the normal route after failed or cancelled activation.
- Restore directory and scoped updater routing alongside the Discord executable.
- Separate tunnel ownership from verified routing. Failed probes and orphan tunnels no longer appear as Connected; Disconnect remains available.
- Show animated connecting/disconnecting progress and operation stages without adding another page.
- Refresh connection health, report launch failures, join cancellation cleanup, and preserve the restored Discord process when Brisa exits.

## 0.1.0-beta.2

First Brisa preview, forked from GoLiveBypass 2.0.9.

- Native Windows interface with light/dark themes and Account/Settings navigation in one window.
- Repaired Proton verification and cancellation handling, with ownership checks before changing a tunnel.
- Windows-only project layout, without the Electron app or plugin distributions.
- Installer and background release updates that wait for a safe restart.

Live native account verification, tunnel routing and normal install/uninstall acceptance still need user testing. The installer is not Authenticode-signed. See [the verification scope](docs/verification.md).
