# Verify the Windows installer script

Both README installation blocks pin the same script revision and check its SHA-256 before execution. Paste the complete block into Windows PowerShell, not Command Prompt (`cmd.exe`).

The installer itself still verifies the Brisa setup file against GitHub's SHA-256 release digest and checks Microsoft's signature before running a missing WebView2 prerequisite. Installer windows stay visible. Those checks do not make the bootstrap script immutable, and Brisa Setup is not Authenticode-signed.

## Pinned, hash-checked bootstrap

Review the [pinned script](https://github.com/insxnsive/brisa/blob/ab6a626bc8c9390b3361dc335a8508a24f3b1f23/scripts/install.ps1), then paste this block into PowerShell on Windows x64. It downloads that exact revision, checks its SHA-256, runs it in Windows PowerShell, and deletes the temporary script afterward. The pinned script still selects the latest published Brisa release.

```powershell
$ErrorActionPreference = 'Stop'
$p = Join-Path ([IO.Path]::GetTempPath()) ('Brisa-install-' + [guid]::NewGuid().ToString('N') + '.ps1')
try {
    Invoke-WebRequest 'https://raw.githubusercontent.com/insxnsive/brisa/ab6a626bc8c9390b3361dc335a8508a24f3b1f23/scripts/install.ps1' -OutFile $p -UseBasicParsing -TimeoutSec 180
    if ((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash -ne '5dbb59c2a0c32c69217a4f7d3ff1e0f541f2a7361919b2c3e5c190fc6bf8c4af') { throw 'Brisa script checksum mismatch. Nothing was executed.' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $p
    if ($LASTEXITCODE -ne 0) { throw "Brisa installer exited with code $LASTEXITCODE." }
} finally { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }
```

This bootstrap does not install WireSock. Brisa offers the vendor's visible installer on the first connection if needed. Review the vendor's terms and Windows prompts.

## If installation fails

- In Command Prompt, `irm ... | iex` fails with `'irm' is not recognized as an internal or external command`. Open Windows PowerShell from Start and paste the complete block above. Do not paste the Markdown fence lines. In Windows Terminal, select a PowerShell tab.
- In PowerShell ISE, run the complete block in the script pane (F5); a partial selection can omit variables or verification steps. Windows PowerShell is the tested shell; PowerShell 7 is optional.
- For HTTPS, TLS or proxy failures, check access to `raw.githubusercontent.com`, `api.github.com`, GitHub release downloads and Microsoft's WebView2 installer. Keep certificate and hash checks enabled. Use approved proxy settings or ask your network administrator for access.
- Keep the exact error text for checksum/signature errors, cancellation or installer exit codes. A parser check alone does not diagnose runtime failures. Do not disable verification.

## WireSock SDK

If Brisa cannot prepare WireSock automatically, obtain **WireSock Secure Connect SDK for Windows x64 (3.4.8.1 or a compatible newer SDK)** from [the vendor's SDK page](https://wiresock.net/wiresock-sdk). The standalone Secure Connect GUI is a different product. Review the vendor terms and approve the visible Windows installation prompt, then retry in Brisa. The README's licensing limitations still apply.

Custom installation folders are supported through WireSock's registered InstallLocation and service/process executable locations. Brisa checks for `wiresock-client.exe` and `wgbooster.dll` together, with matching compatible versions. If discovery fails, the error lists the folders searched (each is checked directly and under `sdk`). Do not copy files into Program Files or reinstall repeatedly; check that the SDK is registered at its actual location.

[Back to the README](../README.md)
