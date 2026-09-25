# Preview verification

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
