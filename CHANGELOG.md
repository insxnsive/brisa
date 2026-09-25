# Changes

## 0.1.0-beta.8

- Fix the packaged backend exiting before status and sign-in become available. Windows PowerShell now compiles the private-storage helper against its own .NET Framework assemblies, not Brisa's bundled .NET assemblies in the application working directory.
- Gate packaging on real production-backend startup from the packaged app directory, with disposable account-free storage. Check fresh startup, restart, status, helper availability and account-request validation without signing in or changing tunnels. The existing UI fixtures alone did not cover this path.
- Preserve the private-storage permission checks, migration safeguards and tunnel-ownership protections.

## 0.1.0-beta.7

- Fix the failed-startup exit loop. A terminal, idle backend failure now offers **Exit Brisa**, and the window close button exits instead of hiding a broken window. Normal working connections still close to the tray.
- Keep possible native-tunnel ownership after an interrupted Connect; an unavailable service cannot authorize an unverified disconnect or update application.
- Separate private native account/configuration storage from the installer directory. Migrate only recognized legacy files, preserve conflicts, and defer migration while WireSock activity is active or unknown.
- Fail current and later requests promptly when the backend transport ends; join concurrent shutdown calls and both output readers. Bound initial status loading and prevent late startup from restarting closed-window polling.
- Save settings atomically, retain the previous state on failure, and restore the previous startup choice if file replacement fails.
- Serialize updater startup/check/disposal, preserve an already-staged update when a newer download fails, suppress late notifications after disposal, and isolate failing UI subscribers.

## 0.1.0-beta.6

- Fix Windows PowerShell module discovery when a PowerShell 7 parent launches Brisa or the installer fixtures; preserve the fail-closed ACL checks and real checksum validation.
- Include the navigation, layout, privacy and installer changes listed below. The beta.5 tag did not publish a release because CI caught this cross-shell environment issue.

## 0.1.0-beta.5 (unpublished candidate)

- Add short same-window page transitions that honor Windows animation preferences and stop cleanly on Back or close.
- Constrain expanded About and Updates content to the Settings viewport; add loaded-window wrapping and hit-target checks in both themes.
- Reject managed data trees with unsafe Windows ACLs or ownership before the backend uses them; retain DPAPI session encryption and document plaintext runtime-profile limits.
- Replace arbitrary helper failure text with fixed, actionable messages. Omit untrusted diagnostic strings while retaining safe status fields.
- Pin both README installation blocks to an immutable script commit and verify its SHA-256 before execution. Harden release metadata checks, cancellation/retry and reboot guidance; include installation scripts in matching-source archives.

## 0.1.0-beta.4 (published 2026-09-25)

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

These remain unsigned previews. The maintainer has accepted the current live connection/routing path; clean-machine prerequisite/account verification, reboot, installed upgrade and uninstall remain open. See [the verification scope](docs/verification.md).
