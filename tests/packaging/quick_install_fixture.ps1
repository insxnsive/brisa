# Run the real quick installer with all network, registry and launch effects stubbed.
param([string]$ScriptPath, [string]$Scenario, [string]$EvidencePath)
$ErrorActionPreference = 'Stop'
$global:present = $Scenario -notlike 'webview-*'
$global:launches = [Collections.Generic.List[string]]::new()
$global:downloads = [Collections.Generic.List[string]]::new()
$global:bytes = [Text.Encoding]::UTF8.GetBytes('Brisa installer test fixture; not an executable')
$sha = [Security.Cryptography.SHA256]::Create()
$global:digest = ([BitConverter]::ToString($sha.ComputeHash($global:bytes))).Replace('-', '').ToLowerInvariant()
$sha.Dispose()
function Get-ItemProperty {
    param($LiteralPath, $Name, $ErrorAction)
    if ($global:present) { return [pscustomobject]@{ pv = '130.0.0.0' } }
    return $null
}
function Invoke-WebRequest {
    param($Uri, $OutFile, [switch]$UseBasicParsing, $TimeoutSec)
    $global:downloads.Add([string]$Uri)
    if ($Scenario -eq 'download-failed') { throw 'Fixture download failed' }
    $data = $global:bytes
    if ($Scenario -eq 'hash-mismatch') { $data = [Text.Encoding]::UTF8.GetBytes('changed fixture') }
    [IO.File]::WriteAllBytes($OutFile, $data)
}
function Invoke-RestMethod {
    param($Uri, $Headers, $TimeoutSec)
    $url = 'https://github.com/insxnsive/brisa/releases/download/v0.1.0-beta.4/Brisa-win-Setup.exe'
    if ($Scenario -eq 'foreign-url') { $url = 'https://example.invalid/Brisa-win-Setup.exe' }
    if ($Scenario -eq 'wrong-tag-url') { $url = $url.Replace('v0.1.0-beta.4', 'v0.0.1') }
    if ($Scenario -eq 'query-url') { $url += '?other=asset' }
    $digest = 'sha256:' + $global:digest
    if ($Scenario -eq 'missing-digest') { $digest = $null }
    $asset = [pscustomobject]@{ name = 'Brisa-win-Setup.exe'; browser_download_url = $url; digest = $digest }
    $assets = @($asset)
    if ($Scenario -eq 'duplicate-asset') { $assets += $asset }
    return @([pscustomobject]@{ tag_name = 'v0.1.0-beta.4'; draft = $false; assets = $assets })
}
function Get-AuthenticodeSignature {
    param($FilePath)
    $status = if ($Scenario -eq 'webview-bad-signature') { 'NotSigned' } else { 'Valid' }
    return [pscustomobject]@{ Status = $status; SignerCertificate = [pscustomobject]@{ Subject = 'CN=Microsoft Corporation, O=Microsoft Corporation' } }
}
function Start-Process {
    param($FilePath, [switch]$Wait, [switch]$PassThru, $ArgumentList, $WindowStyle, $Verb)
    if (-not $Wait -or -not $PassThru -or $ArgumentList -or $WindowStyle -eq 'Hidden') { throw 'Installer must stay visible and wait' }
    $name = [IO.Path]::GetFileName($FilePath)
    $global:launches.Add($name)
    $code = 0
    if ($name -eq 'MicrosoftEdgeWebView2Setup.exe') {
        if ($Scenario -eq 'webview-cancel') { $code = 1602 }
        elseif ($Scenario -ne 'webview-still-missing') { $global:present = $true }
    }
    if ($Scenario -eq 'setup-cancel') { $code = 1602 }
    if ($Scenario -eq 'reboot-required') { $code = 3010 }
    return [pscustomobject]@{ ExitCode = $code }
}
$errorText = $null
try { & $ScriptPath }
catch { $errorText = $_.Exception.Message }
$leftovers = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'Brisa-QuickInstall-*')
[ordered]@{ error = $errorText; launches = @($global:launches); downloads = @($global:downloads); leftovers = $leftovers.Count } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $EvidencePath -Encoding UTF8
