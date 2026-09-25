# Clean Windows acceptance

Use a disposable Windows x64 VM, not a machine with an active tunnel. Do not run these steps against another application's WireSock installation or a personal account profile. Snapshot the VM first.

This checklist records genuine OS/installer acceptance. PowerShell fixtures, loaded WPF tests and portable-update tests do not replace it. Current connection and routing behavior has been accepted by the maintainer; repeating that test is outside the current work.

## Record before testing

- Windows edition/build, display resolution and scale.
- Brisa version, source commit and installer SHA-256.
- Whether WebView2 and the supported WireSock SDK were present before installation.
- Which checks were automated, observed by a person, skipped or blocked.

## Installation and dependencies

1. Copy the checksum-verified PowerShell block from the version's README. Inspect the pinned script before running it.
2. With WebView2 absent, confirm the Microsoft publisher, visible installer and resulting runtime detection. Cancel once and retry. Repeat with the runtime already installed.
3. Install Brisa with its normal visible Setup window. Confirm the Start menu shortcut and installed version. The preview remains unsigned; a checksum is not an Authenticode signature.
4. Test first-use WireSock acquisition only after its licensing terms permit the intended use. A person must review the vendor's terms and UAC prompt. Confirm the pinned installer hash, cancel once, retry, and record any reboot-required result. Do not automate consent.
5. Repeat launch after a VM reboot. Confirm that neither cancelled installation nor a failed download is reported as ready.

## UI and lifecycle

- Check Connection, signed-out Account, expanded verification fields, Settings and expanded About and Updates in both themes.
- Check default and minimum window sizes at 100%, 125%, 150% and 200% actual Windows scaling. Do not infer physical DPI acceptance from simulated content scaling.
- Confirm buttons remain visible or scroll into view, text wraps without horizontal clipping, and footer actions remain reachable.
- Turn Windows animation effects off and confirm page navigation becomes immediate. Turn them back on and confirm a short, finite transition.
- Close to tray, reopen the same window, and use explicit Exit. Do not operate an unrelated tray icon.

## Installed upgrade and removal

1. Install an older Brisa beta in the VM, then stage the candidate update.
2. Verify the update is not applied while the app is busy. Use a safe exit or explicit Restart to Update and check the new installed version.
3. Confirm settings persist and secrets are not copied into release files or diagnostics. Use disposable fixture accounts only; do not attach session or WireGuard files to a report.
4. Uninstall Brisa through Windows Settings. Confirm executable/shortcut removal, document any intentionally retained user data, and verify unrelated software remains untouched.

## Current blocker

The development host has no Windows Sandbox binary, Hyper-V management command or detected VirtualBox/VMware/QEMU executable. No clean guest was available for this pass. Enabling virtualization, installing an OS, rebooting the host or installing a network driver on the host was not attempted.
