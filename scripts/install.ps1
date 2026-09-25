[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Test-WebView2Runtime {
    $keys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )

    foreach ($key in $keys) {
        $entry = Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue
        if ($entry.pv -and [version]$entry.pv -gt [version]'0.0.0.0') { return $true }
    }
    return $false
}

function Invoke-VisibleInstaller([string]$FilePath) {
    $process = Start-Process -FilePath $FilePath -Wait -PassThru
    if ($process.ExitCode -eq 1602) {
        throw 'Installation was cancelled. Run the command again when you are ready.'
    }
    if ($process.ExitCode -in @(1641, 3010)) {
        Write-Host 'Restart Windows before opening Brisa or connecting.'
    }
    if ($process.ExitCode -notin @(0, 1641, 3010)) {
        throw "Installer failed with exit code $($process.ExitCode)."
    }
}

if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ne 'X64') {
    throw 'Brisa currently requires 64-bit Windows (x64).'
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("Brisa-QuickInstall-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    if (-not (Test-WebView2Runtime)) {
        Write-Host 'WebView2 Runtime was not found. Downloading the official Microsoft installer...'
        $webViewInstaller = Join-Path $work 'MicrosoftEdgeWebView2Setup.exe'
        Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webViewInstaller -UseBasicParsing -TimeoutSec 180
        $signature = Get-AuthenticodeSignature -FilePath $webViewInstaller
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
            throw 'The WebView2 installer signature could not be verified as Microsoft-signed.'
        }
        Invoke-VisibleInstaller $webViewInstaller
        if (-not (Test-WebView2Runtime)) {
            throw 'WebView2 Runtime is still missing after its installer completed.'
        }
    } else {
        Write-Host 'WebView2 Runtime is already installed.'
    }

    Write-Host 'Finding the latest published Brisa release...'
    $headers = @{
        'Accept' = 'application/vnd.github+json'
        'User-Agent' = 'Brisa-Quick-Install'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/insxnsive/brisa/releases?per_page=30' -Headers $headers -TimeoutSec 30
    $release = $null
    foreach ($candidate in $releases) {
        if ($candidate.draft) { continue }
        $setupAssets = @($candidate.assets | Where-Object { $_.name -eq 'Brisa-win-Setup.exe' })
        if ($setupAssets.Count -eq 0) { continue }
        if ($setupAssets.Count -ne 1 -or $setupAssets[0].digest -notmatch '^sha256:[0-9a-fA-F]{64}$') {
            throw 'The published Brisa installer metadata is ambiguous or missing a SHA-256 digest.'
        }
        $release = $candidate
        break
    }
    if (-not $release) { throw 'Could not find a published Brisa installer with a SHA-256 digest.' }

    $setupAsset = $release.assets | Where-Object { $_.name -eq 'Brisa-win-Setup.exe' } | Select-Object -First 1
    if ($release.tag_name -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(alpha|beta|rc)\.(0|[1-9]\d*))?$') {
        throw 'The Brisa release tag is not a supported version.'
    }
    $expectedUrl = 'https://github.com/insxnsive/brisa/releases/download/' + $release.tag_name + '/Brisa-win-Setup.exe'
    if (-not [string]::Equals($setupAsset.browser_download_url, $expectedUrl, [StringComparison]::Ordinal)) {
        throw 'The installer URL does not match the selected Brisa release.'
    }

    Write-Host ("Downloading Brisa " + $release.tag_name + ' from its official GitHub release...')
    $setupPath = Join-Path $work 'Brisa-win-Setup.exe'
    Invoke-WebRequest -Uri $setupAsset.browser_download_url -OutFile $setupPath -UseBasicParsing -TimeoutSec 180
    $actualHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $setupAsset.digest.Substring(7).ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw 'The Brisa installer SHA-256 does not match GitHub release metadata; the file was not run.'
    }

    Write-Host 'Checksum verified. Starting Brisa Setup...'
    Invoke-VisibleInstaller $setupPath
    Write-Host 'Brisa setup finished. Open Brisa from the Start menu and choose Run as administrator.'
    Write-Host 'On your first connection, Brisa will offer the official WireSock installer if needed.'
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
