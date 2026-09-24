# Setup

## Requirements

- Windows x64. The interface is designed for Windows 11.
- [WireSock Secure Connect SDK](https://www.wiresock.net/wiresock-secure-connect/download), including its `wiresock-client.exe` command-line client. The inherited integration targets SDK 3.4.8.1; newer versions need compatibility testing.
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) for Proton's verification page.
- A Proton account or your own WireGuard `.conf` profile.

Download prerequisites from their publishers. WireSock has its own license: personal use and commercial use have different terms. Brisa does not bundle or silently install its driver.

You don't need the .NET SDK, Node or Go to use a release build.

## Installer

Download `Brisa-win-Setup.exe` from [Brisa Releases](https://github.com/insxnsive/brisa/releases) and run it. It installs for your Windows account and adds a Start menu shortcut. Open that shortcut with **Run as administrator** to manage WireSock. You can browse the interface without elevation, but connection changes are blocked when ownership cannot be inspected reliably.

The initial builds are not Authenticode-signed. Windows may show an unknown-publisher warning. Check that the download came from this repository; release checksums are in `SHA256SUMS`.

## Portable version

Extract the entire `Brisa-win-Portable.zip` into a folder you can write to. Keep the accompanying files in place and open `Brisa.exe` as administrator. Don't run it from inside the ZIP. The portable package also supports updates.

## First connection

1. If another GoLiveBypass or WireSock instance is connected, disconnect it using that app and quit it first. Brisa will not take over another app's tunnel.
2. Open Account, enter your Proton credentials in the app and complete any verification there. Brisa does not import accounts from the old app.
3. Alternatively, open Settings → Advanced and import your WireGuard profile.
4. Choose Connect. Route selection stays under Route; normal browsing and other apps keep their existing connection.

The main interface is native WPF. WebView2 is used only for Proton verification, not for the application UI.

## Updates and saved data

Brisa checks at startup and periodically while open. It downloads newer published versions and waits until the application can close safely before applying them. Closing to the tray isn't an exit. You can also check in Settings → About and Updates. A normal Exit installs the staged update without reopening the app. Restart to Update closes safely and opens the updated version.

Preview builds accept newer preview releases as well as stable ones. Updates never come from the original GoLiveBypass repository.

Account state and preferences live under `%LOCALAPPDATA%\Brisa`, outside the versioned application files. Updating replaces the program, not those settings. Treat that folder as private; don't include it in bug reports. The saved Proton session can authenticate your account.

## If something fails

- **Another tunnel is active:** disconnect it in its owning app. Don't kill unfamiliar WireSock processes.
- **Ownership cannot be verified:** open Brisa as administrator. It refuses to guess which tunnel belongs to it.
- **Verification won't open:** check that the WebView2 Runtime is installed.
- **Update check failed:** keep using the installed version and try again later. A failed download should not replace a working installation.

For reports, include your Brisa version, Windows version and the visible error. Never share credentials, verification codes, session files or the contents of a `.conf` profile.
