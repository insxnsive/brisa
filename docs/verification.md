# Preview verification

## Beta.7 repair candidate

The new failed-startup report exposed a different path from the earlier successful routing tests: the installer and native state shared a parent directory, so recursive private-data validation could reject the installed payload. A terminal backend then left the exit guard waiting for a service that could never acknowledge cancellation. This was not evidence that Discord was still running.

The repair moves native state into a dedicated private child, retains fail-closed ownership checks, and permits an idle terminal-backend window to close without authorizing an update. Settings persistence, updater lifecycle, initial-status cancellation and transport EOF/disposal also received regression fixes.

The full offline runner passed both directly and through a checksum-verified portable PowerShell 7 parent: 40 packaging tests; 77 backend tests passed and one elevation-only fixture skipped; the Go suites; 23 core tests; 85 navigation, 150 appearance and 57 lifecycle assertions; and C#–Node transport/process integration. Storage fixtures cover opaque legacy migration, existing conflicts, unsafe ACL/reparse rejection, active/unknown WireSock refusal, failed-publication rollback/retry, read-only matching-destination refusal/retry, interrupted source-deletion recovery, and real Windows handle exclusion against file writes and directory replacement. Administrators-owner SID acceptance passed a decision test; creating that ownership with an elevated token is not verified locally.

The first beta.7 CI run exposed inherited PowerShell 7 module paths in an elevation-only ACL test helper. The test helper now isolates Windows PowerShell's module path, treats command errors as terminating, and verifies the assigned owner SID. A non-elevated ACL read/write/read regression reproduces the same shell boundary locally; it failed before the fixture repair and passes after it.

The rebuilt executable also passed 41 failed-startup UI assertions: both themes, primary Exit and window-close paths at actual 100%/125% scaling, Settings Save returning to the same error screen without losing its actionable message, normal process termination, and preservation of pre-existing Brisa process identities. This local executable check is not yet evidence for a published beta.7 artifact.

No real credentials/session/profile contents were read. Independent C# and storage follow-up reviews found no remaining concrete blocking security or logic issue. All new and matching destinations are opened writable and flushed before held originals are removed; fault fixtures cover partial deletion and retry, not a physical power-loss test. The installed app and existing tunnel were not replaced or stopped. Clean-machine installation, real elevated startup/migration, higher-DPI acceptance, packaged two-factor acceptance, WireSock terms and trusted signing remain separate open gates. Final publication/packaged acceptance is recorded below only when completed.

## Published beta.6

[v0.1.0-beta.6](https://github.com/insxnsive/brisa/releases/tag/v0.1.0-beta.6) is a published, unsigned prerelease from commit `1e109ab866681b44baf1623c0ce7293990d5ecec`. The failed beta.5 tag was retained without rewriting it; beta.5 never became a published release.

- [Branch CI](https://github.com/insxnsive/brisa/actions/runs/36101584040) and [tag/release CI](https://github.com/insxnsive/brisa/actions/runs/36102141558) passed, including the offline suites, packaging, disposable portable upgrade and release validation.
- All eight published assets were downloaded and checked against GitHub's recorded sizes and SHA-256 digests. The update feed, package manifest and bundled/separate corresponding source also verified.
- The CI-built portable executable passed the same 16 no-network UI combinations and 464 assertions described below, at actual 100%/125% scaling. All 479 executable/DLL files matched the published portable payload byte-for-byte. Only the bundled source ZIP and its manifest differed between the two builds.
- The real Velopack `GithubSource`, without an access token, selected beta.6 for a beta.4 test locator and downloaded the package with a matching hash. Current-version reinstall and downgrade checks passed. This used an isolated cache, not the installed application.
- An independent follow-up source review found no remaining blocking security or logic issue.

Beta.5 CI exposed a PowerShell 7 parent passing its module paths through Python into Windows PowerShell, hiding `Get-FileHash`. Reproduction with a checksum-verified portable PowerShell 7 runtime also exposed the same problem in the ACL reader's `Get-Acl` call. Beta.6 lets those child shells construct their native module paths; it does not replace the hash command or relax ACL decisions.

Two new regressions bring the packaging suite to 40 tests. The complete offline runner passed both normally and through a real PowerShell 7 parent: 40 packaging tests, 65 backend tests, the Go suites, 21 core tests, 77 navigation assertions, 150 appearance assertions and C#–Node integration. The direct-handoff regression confirms that PowerShell 7's direct `powershell.exe` launch accepts a matching hash and rejects a mismatch without changing its inherited module path; the Python/Node-intermediary case is distinct.

No live account, installation or tunnel was operated. Actual 150%/200% scaling, packaged two-factor challenge acceptance and the clean-machine checklist remain open. WireSock production-use permission and trusted publisher signing remain separate stable-release gates.

## Beta.5 local verification (2026-09-25)

The beta.5 changes passed the full offline runner:

- 38 packaging, release, quick-install and UI contract tests.
- 65 Node backend tests, including real NTFS ACL/ownership readback on disposable files, plus the retained Go helper suites.
- 21 native core tests, 77 inline-navigation assertions and 150 loaded-window appearance assertions.
- Real C# to Node request/response and process-lifecycle integration checks.

The packaged Windows executable was exercised in explicit no-network mode at **100% and 125% actual Windows display scaling**. Both themes, signed-in/out fixture states, and 460×540 / 440×520 DIP sizes passed: 16 combinations and 464 assertions. The run covered username input, Account/Back, Settings expansion and scrolling in both directions, Cancel/Save, Advanced diagnostics/Close and Route/Cancel. Screenshots showed no unintended overlap or inaccessible footer actions. Partial content at the top of a scrolled Settings viewport is not missing content.

Settings entrance motion changed the page pixels and settled without looping in every combination. Comparisons exclude native title-bar activation fades and the fixed navigation header's control-state animations. Loaded-window tests separately cover the 170 ms transition, reduced-motion policy, rapid Back cleanup, expanded two-factor fields and long update messages. Status-text pressure was tested up to 200%; that is not an OS DPI test. Actual 150%/200% scaling and packaged two-factor challenge acceptance remain outstanding.

A disposable portable **0.0.1 → 0.1.0-beta.5** update applied successfully, rendered before and after, and preserved its test data. The feed, package manifest, checksums and bundled/separate source archives verified. Setup was queried for help only; no real installation occurred. These local packaged checks used source commit `7ef9a05768cede75a4d2b1fde7a4b4c887939e31`; subsequent verification-note edits do not change the runtime code. Tag CI rebuilds and rechecks the final source before publication.

An independent source review found no remaining blocking security or logic issue after the ACL-ownership regression was repaired. The [privacy audit](privacy.md) explains the limits of DPAPI and plaintext runtime profiles. Both README bootstrap blocks pin an immutable installer-script commit and verify its independently embedded SHA-256 before execution; fixture tests reject tampering and download failures without launching an installer.

No real account/session/profile content was read, and the active Brisa instance and tunnel were not operated. Clean-machine install/UAC/reboot/installed-upgrade/uninstall, higher-DPI acceptance, WireSock production permission and trusted publisher signing remain open. This is preview evidence, not stable-release approval.

## Published beta.4 (2026-09-25)

[v0.1.0-beta.4](https://github.com/insxnsive/brisa/releases/tag/v0.1.0-beta.4) is a published GitHub prerelease, not an unreleased change. Its on-demand WireSock path passed source/fixture checks and packaging; the real vendor installer was not run during those checks.

The maintainer subsequently reported the current connection/routing path all green. This is user-observed acceptance, not a new automated live test. The present work does not repeat that test, read real credentials, or disturb the active tunnel.

A genuine clean Windows install, first-run prerequisite/UAC/cancel-and-retry flow, reboot, installed upgrade and uninstall remain unverified. The development host has no available disposable Windows VM/Sandbox; fixtures and portable updates are not substitutes. The [clean-machine checklist](clean-machine-acceptance.md) records the outstanding boundary. WireSock production-use clarification and trusted publisher signing are separate unresolved stable-release gates.

## Connection repair candidate (0.1.0-beta.3)

The connection repair has passed the full offline runner:

- 24 packaging, release and UI contract tests.
- 55 Node backend tests, including real PowerShell batch parsing against stubbed process APIs and launcher-versus-client discovery, plus the retained Go helper suites.
- 21 native core tests, 66 navigation assertions and 94 loaded-window appearance assertions.
- Real C# to Node transport and child-process survival checks.
- Packaged executable UI Automation: Connect and Disconnect were invoked in explicit no-network test mode, the app exited, and two captured frames differed in the progress-bar region.
- Disposable portable update, feed selection, checksums, and no-reinstall/no-downgrade checks.

An independent review identified cancellation-join, unverified-success, updater-scoping, process-disposal, and polling-budget issues. Each was repaired with regression coverage. No-network UI mode is not live-routing proof; live acceptance is recorded separately.

The user confirmed the rebuilt beta.3 connection works. This is user-observed live acceptance, separate from the earlier automated route-probe failures.

Tray/onboarding regression coverage includes legacy `CloseToTray=false`, X while connected or connecting, reopening without disconnect/cancel/disposal, explicit Exit cleanup, signed-out/signed-in/custom-profile startup, signup navigation and browser-launch failure fallback. The full offline runner passed after these changes. Packaged UI Automation verified first-run sign-in, the accessible registration link, signed-in Home, X hiding the window while preserving its registered tray icon, tray reopening of the same HWND, simulated connection survival, and explicit tray Exit. The external-browser click was not completed because its approval timed out; the navigation handler and official URL were exercised through an injected browser launcher.

A separately authorized live stop-and-reopen diagnostic reproduced `PROCESS_IDENTITY_CHANGED` after Discord’s parent exited while a child was still represented by WMI. Shutdown now holds an OS process handle, rechecks the executable path, and tolerates an error only when that held process is confirmed exited. Real PowerShell tests cover dying children, still-live unreadable identities, changed identities, and denied termination. After the fix, another authorized live diagnostic confirmed no Discord processes remained after stop and a visible client returned after launch. Neither check changed the tunnel, inspected credentials, or replaced the running Brisa instance.

## First preview

The first Brisa preview has passed these local checks:

- 24 packaging, release and UI contract tests.
- 19 Node backend tests and the retained Go helper test suites.
- 20 native core tests, 47 navigation assertions and 94 loaded-window appearance assertions.
- C# to Node request/response integration checks.
- A real portable upgrade from a disposable `0.0.1` build to `0.1.0-beta.1`, with rendered launches before and after, package checksum verification and preservation of a test file outside the versioned app directory.
- A build from the source ZIP without a Git checkout.
- Independent review of update shutdown ordering and the release scripts.

The packaged executable was also inspected in isolated UI mode. A manual desktop-click check was not completed. No live account, existing tunnel or network driver was changed.

Setup's command-line entry point was checked without installation. A normal install/uninstall, installed-app upgrade, real Proton verification and live tunnel routing still need acceptance on a suitable test machine. The preview is unsigned. These checks are not a claim of production readiness.

The GitHub workflow reruns the offline suites, packages the app and exercises the disposable portable upgrade before publishing a version. See the run linked to the release commit for its result.
