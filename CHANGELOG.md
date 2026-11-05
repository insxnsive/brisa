# Changes

## 0.1.0-beta.10

- Rebuild Home around one surface: status, route and traffic sit in a single card, with the primary action below it and the traffic card keeping a fixed height so the layout does not jump between states.
- Apply one colour rule. Coral marks an action, teal marks a connected or protected state, and every other state is neutral grey. The same rule covers both themes.
- Rebuild Settings as a General section with theme, start with Windows, connection details and language, plus the tray note. The page no longer scrolls at the default window size.
- Rework Advanced so the support log explains what it contains before you generate it, and an imported configuration that Brisa rejects says why in plain language.
- Put the server and its ping on the country's line, with the long country name trimming first so the server and the ping stay whole, and explain through a tooltip why the route is locked while connected.
- Translate the whole interface into Brazilian Portuguese. Language sits in Settings with System, English and Portuguese (Brazil); System follows Windows, and a change repaints the open window without closing it.
- Turn every backend failure into a code the interface translates, so a Portuguese user reads a Portuguese reason and never the backend's English sentence.
- Format numbers and dates with the chosen language. Durations and log timestamps stay invariant, so a log reads the same on any machine.
- Leave product names, the GPL text, the vendor installer and the generated log in English by design, and take country names from one bundled CLDR table instead of translating them one by one.
- Name the route, the traffic chart and the direction glyphs for screen readers, keep focus rings visible in both themes, and meet WCAG AA contrast throughout.
- Animate only a change of state, over 150 to 200 ms, and honour the Windows animation setting.

## 0.1.0-beta.9

- Refuse to run a WireSock binary that is not signed by the vendor or that sits in a folder a normal user can write to. Discovery reads the machine-wide install only, checks the executable and its booster, compares the certificate subject exactly, and re-checks both immediately before every elevated use.
- Keep a stop that already confirmed the tunnel is down from failing: an optional global action that cannot be verified is skipped instead of turning the result into "still running", and the network-lock reset, DNS reset and cache flush each stay inside their own budget.
- Pause the three-minute connect deadline while the vendor installer is open, with a thirty-minute ceiling and an explicit timeout message instead of a silent cancellation.
- Stop trapping the user in a window that cannot close. When the installer state is unknown the exit path asks "Exit Brisa anyway?" and says Discord was closed; the backend refuses cancellation while supervision is lost instead of reporting a normal cancel.
- Bound every backend call. The stop reserves ten seconds for its final check, the residual read always carries a real timeout, and the exit path no longer runs the snapshot, the disconnect and the confirming snapshot on one shared token.
- Report a snapshot that overlapped a connect or a disconnect as stale instead of publishing a pre-tunnel reading, and re-check freshness at every use rather than once after the inspection.
- Probe WireSock once per selection, asynchronously, with the trust verdict included. Connection setup went from about 3 s to about 1.4 s on the development machine.
- Close Discord before stopping the tunnel and put it back on the normal route afterwards. If Discord cannot be found, stop the tunnel anyway and record the failure rather than leaving the tunnel running.
- Sweep stale cancellation markers left by earlier attempts, and share one inspection between concurrent snapshots so a slow query does not open a second one.
- Remove the dead tunnel and route orchestration code left behind by the earlier installer work.

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
- Add short same-window page transitions that honor Windows animation preferences and stop cleanly on Back or close.
- Constrain expanded About and Updates content to the Settings viewport; add loaded-window wrapping and hit-target checks in both themes.
- Reject managed data trees with unsafe Windows ACLs or ownership before the backend uses them; retain DPAPI session encryption and document plaintext runtime-profile limits.
- Replace arbitrary helper failure text with fixed, actionable messages. Omit untrusted diagnostic strings while retaining safe status fields.
- Pin both README installation blocks to an immutable script commit and verify its SHA-256 before execution. Harden release metadata checks, cancellation/retry and reboot guidance; include installation scripts in matching-source archives.

These changes first shipped in beta.6. The beta.5 candidate below was never published.

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
