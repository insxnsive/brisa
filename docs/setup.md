# Setup

## Requirements

- Windows x64. The interface is designed for Windows 11.
- [WireSock Secure Connect SDK](https://wiresock.net/wiresock-sdk), including its `wiresock-client.exe` command-line client. If missing, Brisa fetches pinned version 3.4.8.1 directly from WireSock on first connection, verifies the installer SHA-256, then opens the vendor's installer and Windows elevation prompts. Newer versions may work, but compatibility has not been tested.
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) for Proton's verification page.
- A Proton account or your own WireGuard `.conf` profile.

Download prerequisites from their publishers. The WireSock SDK Free tier is described as non-commercial evaluation or internal proof-of-concept use, not production. Brisa's non-commercial status and direct vendor download do not by themselves establish permission for a public production release; written clarification remains a stable-release gate. Brisa does not bundle the SDK. Its first-connection flow opens the pinned official installer so the user can review the vendor's terms and Windows elevation prompt.

You don't need the .NET SDK, Node or Go to use a release build.

## Installer

Download `Brisa-win-Setup.exe` from [Brisa Releases](https://github.com/insxnsive/brisa/releases) and run it. It installs for your Windows account and adds a Start menu shortcut. Open that shortcut with **Run as administrator** to manage WireSock. You can browse the interface without elevation, but connection changes are blocked when ownership cannot be inspected reliably.

The initial builds are not Authenticode-signed. Windows may show an unknown-publisher warning. Check that the download came from this repository; release checksums are in `SHA256SUMS`.

## Portable version

Extract the entire `Brisa-win-Portable.zip` into a folder you can write to. Keep the accompanying files in place and open `Brisa.exe` as administrator. Don't run it from inside the ZIP. The portable package also supports updates.

## First connection

1. If another GoLiveBypass or WireSock instance is connected, disconnect it using that app and quit it first. Brisa will not take over another app's tunnel.
2. When no Proton account is signed in, Brisa opens the Account screen automatically. Enter your credentials in the app and complete any verification there. Create a Free Account opens Proton’s official registration page in your browser. Brisa does not import accounts from the old app.
3. Alternatively, open Settings → Advanced and import your WireGuard profile.
4. Choose Connect. On first use, if WireSock isn't installed, Brisa downloads the verified SDK installer from WireSock and opens it for you; complete its license and Windows elevation prompts. Brisa then continues the connection. Route selection stays under Route; normal browsing and other apps keep their existing connection.

The main interface is native WPF. WebView2 is used only for Proton verification, not for the application UI.

The window’s X button normally hides Brisa in the tray without disconnecting or restarting Discord. Double-click its tray icon or choose Open to return. Choose Exit from the tray menu when you want to disconnect and quit. If the native service failed before the first status loaded and no native connection may be active, **Exit Brisa** and X close the failed window instead. That fallback never applies an update without a verified shutdown.

## Updates and saved data

Brisa checks at startup and periodically while open. It downloads newer published versions and waits until the application can close safely before applying them. Closing to the tray isn't an exit. You can also check in Settings → About and Updates. A normal Exit installs the staged update without reopening the app. Restart to Update closes safely and opens the updated version.

Preview builds accept newer preview releases as well as stable ones. Updates never come from the original GoLiveBypass repository.

Preferences remain in `%LOCALAPPDATA%\Brisa\settings.json`; native account state and profiles use the private `%LOCALAPPDATA%\Brisa\native-data` child directory. The parent is also the installer base, so Brisa does not treat installed application files as account data or recursively rewrite their permissions. Updating replaces the program, not those settings. Do not include either data location in bug reports. Proton sessions use Windows current-user DPAPI protection; runtime WireGuard profiles remain plaintext and require private NTFS permissions.

On first use after upgrading an older preview, Brisa migrates only its recognized account, state and profile files. Conflicting destinations and active or unknown WireSock state defer migration rather than overwrite data or interrupt a tunnel. Finish the existing connection normally before retrying; do not delete account/profile files to bypass this protection. Existing unsafe private-store ACLs, owners and reparse points are rejected. Sign-out retains explicitly imported custom profiles. See [privacy and local data](privacy.md).

## If something fails

- **Another tunnel is active:** disconnect it in its owning app. Don't kill unfamiliar WireSock processes.
- **Ownership cannot be verified:** open Brisa as administrator. It refuses to guess which tunnel belongs to it.
- **Verification won't open:** check that the WebView2 Runtime is installed.
- **Update check failed:** keep using the installed version and try again later. A failed download should not replace a working installation.

For reports, include your Brisa version, Windows version and the visible error. Never share credentials, verification codes, session files or the contents of a `.conf` profile.
