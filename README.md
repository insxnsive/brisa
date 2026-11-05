<p align="center">
  <img src="src/Brisa/Assets/Brisa.png" width="112" alt="Brisa">
</p>

<h1 align="center">Brisa</h1>

<p align="center">
  <a href="https://github.com/insxnsive/brisa/releases"><img src="https://img.shields.io/github/v/release/insxnsive/brisa?style=flat-square" alt="release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/insxnsive/brisa?style=flat-square" alt="license"></a>
  <a href="#requirements"><img src="https://img.shields.io/badge/platform-Windows%20x64-blue?style=flat-square" alt="platform"></a>
  <a href="docs/development.md"><img src="https://img.shields.io/badge/built%20with-.NET%2010-512BD4?style=flat-square" alt="built with"></a>
</p>

**Discord goes through WireGuard. Everything else stays on your normal connection.** No VPN for your browser, your games or the rest of the PC, and no Electron wrapper. Built with .NET and WPF, interface in English and Portuguese (Brazil).

[Download](https://github.com/insxnsive/brisa/releases) · [Install](#install) · [Features](#features) · [Usage](#usage) · [Requirements](#requirements) · [Docs](#docs) · [License](#license) · [Português (Brasil)](README.pt-BR.md)

## Install

Download **`Brisa-win-Setup.exe`** from [Releases](https://github.com/insxnsive/brisa/releases), or take the portable ZIP from the same page.

The Windows setup is not code-signed yet, so SmartScreen may warn about an unknown publisher. If the file came from this repository's Releases, choose **More info → Run anyway**.

Brisa installs per user without administrator rights; the WebView2 and WireSock installers it may open can ask for them. Nothing installs silently.

Prefer a script that checks the download first? In Windows PowerShell (not Command Prompt):

```powershell
irm https://raw.githubusercontent.com/insxnsive/brisa/brisa/scripts/install.ps1 | iex
```

It verifies Microsoft's signature on WebView2 and the SHA-256 of the Brisa setup before running either. The pinned bootstrap, and what each check does and does not prove: [install checks](docs/install.md).

## Features

- Only Discord takes the tunnel. Your browser, games and everything else keep the normal route.
- Proton sign in, or import a WireGuard `.conf` you already have.
- Closing the window leaves Brisa and the connection running in the tray.
- Updates download in the background and install when you exit Brisa.

## Usage

1. Open Brisa from the Start menu. Administrator rights are needed to connect, not to browse the app.
2. Sign in to your Proton account, or import your profile under Settings → Advanced.
3. Pick a route if you want one, then press Connect.

Connecting and disconnecting close and reopen Discord, whichever version you run. Leave any call first; on disconnect Brisa reopens Discord on the normal route.

If WireSock is missing, Brisa opens the official installer on the first connection and continues afterwards. More in [setup](docs/setup.md).

## Requirements

- Windows x64, built for Windows 11.
- Administrator rights to connect or disconnect.
- [WireSock Secure Connect SDK](https://wiresock.net/wiresock-sdk), fetched on the first connection if missing.
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) for Proton's verification page.
- A Proton account or your own WireGuard `.conf`.
- No other WireSock or GoLiveBypass tunnel running. Brisa never takes over another app's tunnel.

## Docs

| | |
| --- | --- |
| [Install checks](docs/install.md) | Pinned bootstrap, hash verification |
| [Setup](docs/setup.md) | Requirements, installer, portable build, first connection, [window and tray](docs/setup.md#window-and-tray), saved data |
| [Development](docs/development.md) | Build, test, package, macOS branch |
| [Verification](docs/verification.md) | What was tested and how |
| [Privacy](docs/privacy.md) | What Brisa stores and what it never sends |
| [Releasing](docs/releasing.md) | Version bumps, tags, update feed |
| [Third-party notices](THIRD-PARTY-NOTICES.md) | Licenses of everything inside |

## Contributing

Bugs and suggestions go to [Issues](https://github.com/insxnsive/brisa/issues). Never post passwords, verification codes, session files or VPN profiles there. The log from Advanced → Generate Log is safe to share: account names, paths and addresses are stripped before it is written.

## License

[GPL-3.0-or-later](LICENSE), no warranty. Brisa is a native fork of [GoLiveBypass](https://github.com/bezumiya/GoLiveBypass). The Electron interface and plugin were removed and the original copyright notices kept. The Proton profile helper comes from [hatemosphere/protonvpn-wg-confgen](https://github.com/hatemosphere/protonvpn-wg-confgen).

Brisa is not affiliated with Discord, Proton or WireSock.
