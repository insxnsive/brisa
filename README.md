# Brisa

Brisa routes Discord through WireGuard without putting the rest of your PC on a VPN. It has a small native Windows interface built with .NET 10 and WPF.

This is a Windows-only fork of [GoLiveBypass](https://github.com/bezumiya/GoLiveBypass). The Electron interface and plugin were removed. Brisa keeps the networking code it needs, including the Node coordinator and Go Proton helper.

## Install

1. Download `Brisa-win-Setup.exe` from [Releases](https://github.com/insxnsive/brisa/releases).
2. Run the installer, then open Brisa as administrator.
3. Open Account to sign in to Proton, or import a WireGuard profile in Settings → Advanced.
4. Choose Connect when you're ready.

You need Windows x64, a compatible WireSock Secure Connect SDK installation, and Microsoft's WebView2 Runtime for Proton verification. The .NET and Node runtimes are included. Brisa does not install network drivers or connect on first launch.

See the [setup guide](docs/setup.md) for prerequisites, portable use and troubleshooting.

## Updates

Brisa checks for new releases automatically and downloads them in the background. Updates wait for a safe restart; they don't interrupt an active connection. Only published Brisa versions are installed, never arbitrary commits or upstream GoLiveBypass builds.

## Build

The [development guide](docs/development.md) covers dependencies, tests and packaging. Release downloads include matching source in `Brisa-<version>-source.zip`.

## Credits and license

Based on GoLiveBypass by [bezumiya](https://github.com/bezumiya) and its contributors. The Proton helper comes from [hatemosphere/protonvpn-wg-confgen](https://github.com/hatemosphere/protonvpn-wg-confgen), with changes inherited from GoLiveBypass and additional fixes here. Brisa's native interface and maintenance are by [insxnsive](https://github.com/insxnsive).

Brisa is licensed under [GPL-3.0-or-later](LICENSE), without warranty. Original copyright notices remain in the source. See [third-party notices](THIRD-PARTY-NOTICES.md) for dependency licenses. Brisa is not affiliated with Discord, Proton or WireSock.

The first releases are previews. Live Proton verification and tunnel behavior still need wider testing on the native host. Please report problems in [this repository](https://github.com/insxnsive/brisa/issues), without passwords, tokens or VPN configuration files.
