# Credits and third-party notices

## Brisa and GoLiveBypass

Brisa is a modified version of [GoLiveBypass](https://github.com/bezumiya/GoLiveBypass), by bezumiya and contributors, based on release 2.0.9 (`c181f39c7bf3d102770de72ea97572bb37d155c5`). The original project's Git history and source copyright notices are preserved.

Modifications by insxnsive, September 24, 2026: native .NET/WPF interface, Proton verification and lifecycle fixes, Windows-only source extraction, Brisa branding, installer and automatic release updates. The Electron application, plugin and unrelated platform/distribution code are not part of Brisa's current source tree.

The application is distributed under GNU GPL version 3 or, at your option, any later version. See `LICENSE`. You may redistribute and modify it under those terms. It comes without warranty, including warranties of merchantability or fitness for a particular purpose.

## Proton helper

`tools/proton-confgen` derives from [hatemosphere/protonvpn-wg-confgen](https://github.com/hatemosphere/protonvpn-wg-confgen), with GoLiveBypass and Brisa changes. Its original GPLv3 license is retained. Vendored Go dependency sources and notices are included in the release source archive; binary distributions also contain their license notices under `licenses/Go`.

## Runtime and UI components

- Microsoft .NET and WPF: MIT license and bundled third-party notices.
- Node.js: MIT and bundled third-party licenses, included as `licenses/Node-LICENSE.txt`.
- Velopack: MIT license. Provides the installer and update mechanism.
- Microsoft WebView2 SDK: BSD-3-Clause, with Microsoft's copyright and notice included. The separately installed Edge WebView2 Runtime has its own terms.
- Other NuGet dependencies: package license metadata and available license/notice files are included under `licenses/NuGet`.

Build tooling is declared in the locked npm and .NET tool manifests. The application does not include Electron.

## External software and services

WireSock Secure Connect is installed separately and is not relicensed by Brisa. Its publisher distinguishes personal/non-commercial use from commercial use; see [WireSock licensing](https://www.wiresock.net/wiresock-secure-connect/licensing).

Discord and Proton are independent services with their own terms. Brisa is not sponsored by or affiliated with their publishers or with WireSock.
