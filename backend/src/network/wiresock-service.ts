import { quotePowerShell } from "./powershell.ts";

/**
 * Runs a PowerShell file through UAC without copying the file contents into
 * an encoded command-line argument. Windows has a relatively small command
 * line limit; the WireSock service script is intentionally detailed enough
 * to exceed it when the script is nested in the elevation wrapper.
 */
export function elevatedPowerShellFileArgs(scriptPath: string, resultPath?: string): string[] {
  if (!scriptPath || /["\r\n\0]/.test(scriptPath) || (resultPath !== undefined && (!resultPath || /["\r\n\0]/.test(resultPath)))) throw new Error("Caminho do script PowerShell inválido");
  const literal = quotePowerShell;
  // A worker redirecting a long-running child's output can stay alive with it.
  // For direct mode, wait for the explicit result, not for that worker to exit.
  if (resultPath) {
    const wrapper = `$ErrorActionPreference='Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$launch = @{FilePath='powershell.exe'; WindowStyle='Hidden'; PassThru=$true; ArgumentList=@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File', ('"' + ${literal(scriptPath)} + '"'))}
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { $launch.Verb = 'RunAs' }
$worker = Start-Process @launch
$deadline = (Get-Date).AddSeconds(100)
do {
  if (Test-Path -LiteralPath ${literal(resultPath)}) {
    $result = [IO.File]::ReadAllText(${literal(resultPath)})
    if ($result -match '^(0|1)\\r?\\n.+') { exit [int]$Matches[1] }
  }
  $worker.Refresh()
  if ($worker.HasExited) { throw 'DIRECT_WORKER_EXITED: worker encerrou sem resultado' }
  Start-Sleep -Milliseconds 100
} while ((Get-Date) -lt $deadline)
Stop-Process -Id $worker.Id -Force -ErrorAction SilentlyContinue
throw 'DIRECT_WORKER_TIMEOUT: sem resultado de ativacao'`;
    return ["-NoProfile", "-NonInteractive", "-EncodedCommand", Buffer.from(wrapper, "utf16le").toString("base64")];
  }
  const wrapper = `$ErrorActionPreference='Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$scriptPath = ${literal(scriptPath)}
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $scriptPath
  exit $LASTEXITCODE
}
$child = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -Wait -PassThru -ArgumentList @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File', ('"' + $scriptPath + '"'))
exit $child.ExitCode`;
  return ["-NoProfile", "-NonInteractive", "-EncodedCommand", Buffer.from(wrapper, "utf16le").toString("base64")];
}
