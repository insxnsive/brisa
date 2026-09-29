# Verify the Windows installer script

The README's one-line command downloads and runs the current installer script from the project's `brisa` branch. It trusts this repository and GitHub HTTPS; it does not pin or separately hash the script before executing it.

The installer itself still verifies the Brisa setup file against GitHub's SHA-256 release digest and checks Microsoft's signature before running a missing WebView2 prerequisite. Installer windows stay visible. Those checks do not make the bootstrap script immutable, and Brisa Setup is not Authenticode-signed.

## Pinned, hash-checked alternative

If you want to verify the bootstrap script as well, review the [pinned script](https://github.com/insxnsive/brisa/blob/ab6a626bc8c9390b3361dc335a8508a24f3b1f23/scripts/install.ps1), then paste this block into PowerShell on Windows x64. It downloads that exact revision, checks its SHA-256, runs it in Windows PowerShell, and deletes the temporary script afterward. The pinned script still selects the latest published Brisa release.

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

Neither command installs WireSock. Brisa offers the vendor's visible installer on the first connection if needed. Review the vendor's terms and Windows prompts.

[Back to the README](../README.md)
