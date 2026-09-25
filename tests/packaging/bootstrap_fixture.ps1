# Run only the README bootstrap with downloads and process launch stubbed.
param([string]$Bootstrap, [string]$Source, [string]$Scenario, [string]$Evidence)
$ErrorActionPreference = 'Stop'
$global:invoked = $false
function Invoke-WebRequest {
    param($Uri, $OutFile, [switch]$UseBasicParsing, $TimeoutSec)
    if ($Scenario -eq 'download-failed') { throw 'Fixture download failure' }
    if ($Scenario -eq 'tampered') { [IO.File]::WriteAllText($OutFile, 'throw "Must never run"') }
    else { [IO.File]::WriteAllBytes($OutFile, [IO.File]::ReadAllBytes($Source)) }
}
function powershell.exe {
    param([switch]$NoProfile, $ExecutionPolicy, $File)
    if (-not $NoProfile -or $ExecutionPolicy -ne 'Bypass' -or -not (Test-Path -LiteralPath $File)) { throw 'Bad local invocation' }
    $global:invoked = $true
    $global:LASTEXITCODE = 0
}
$errorText = $null
try { & $Bootstrap } catch { $errorText = $_.Exception.Message }
$leftovers = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'Brisa-install-*.ps1')
[ordered]@{ invoked = $global:invoked; error = $errorText; leftovers = $leftovers.Count } |
    ConvertTo-Json | Set-Content -LiteralPath $Evidence -Encoding UTF8
