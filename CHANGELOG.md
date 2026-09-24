# Changes

## 0.1.0-beta.1

First Brisa preview, forked from GoLiveBypass 2.0.9.

- Native Windows interface with light/dark themes and Account/Settings navigation in one window.
- Repaired Proton verification and cancellation handling, with ownership checks before changing a tunnel.
- Windows-only project layout, without the Electron app or plugin distributions.
- Installer and background release updates that wait for a safe restart.

Live native account verification, tunnel routing and normal install/uninstall acceptance still need user testing. The installer is not Authenticode-signed. See [the verification scope](docs/verification.md).
