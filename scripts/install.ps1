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
        Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $webViewInstaller -UseBasicParsing
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
    $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/insxnsive/brisa/releases?per_page=30' -Headers $headers
    $release = $null
    foreach ($candidate in $releases) {
        if ($candidate.draft) { continue }
        $setupAsset = $candidate.assets | Where-Object { $_.name -eq 'Brisa-win-Setup.exe' } | Select-Object -First 1
        if ($setupAsset -and $setupAsset.digest -match '^sha256:[0-9a-fA-F]{64}$') {
            $release = $candidate
            break
        }
    }
    if (-not $release) { throw 'Could not find a published Brisa installer with a SHA-256 digest.' }

    $setupAsset = $release.assets | Where-Object { $_.name -eq 'Brisa-win-Setup.exe' } | Select-Object -First 1
    $assetUri = [Uri]$setupAsset.browser_download_url
    if ($assetUri.Scheme -ne 'https' -or $assetUri.Host -ne 'github.com' -or
        -not $assetUri.AbsolutePath.StartsWith('/insxnsive/brisa/releases/download/', [StringComparison]::Ordinal)) {
        throw 'The release installer URL is not an approved Brisa GitHub release asset.'
    }

    Write-Host ("Downloading Brisa " + $release.tag_name + ' from its official GitHub release...')
    $setupPath = Join-Path $work 'Brisa-win-Setup.exe'
    Invoke-WebRequest -Uri $setupAsset.browser_download_url -OutFile $setupPath -UseBasicParsing
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
