import { execFile } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import { elevatedPowerShellFileArgs } from "./network/wiresock-service.ts";
import { findWireSockCandidate, inspectWireSockAsync } from "./network/vpn-windows.ts";
import { formatAllowedApps, sanitizeWireGuardConfig, validateWireGuardConfig } from "./network/vpn-types.ts";

const quotePowerShell = (value: string) => `'${value.replace(/'/g, "''")}'`;

export function nativeDirectScript(executable: string, configPath: string, resultPath: string, cancelPath: string): string {
  const literal = quotePowerShell;
  return `\uFEFF$ErrorActionPreference='Stop'
$resultPath=${literal(resultPath)}
$cancelPath=${literal(cancelPath)}
$stdoutPath=${literal(`${resultPath}.stdout`)}
$stderrPath=${literal(`${resultPath}.stderr`)}
function Complete([int]$code,[string]$detail){
  try { [IO.File]::WriteAllText(($resultPath+'.tmp'),("$code"+[Environment]::NewLine+"$detail"),[Text.UTF8Encoding]::new($false)); Move-Item -LiteralPath ($resultPath+'.tmp') -Destination $resultPath -Force } catch {}
  exit $code
}
function Assert-NotCancelled { if(Test-Path -LiteralPath $cancelPath){ throw 'CANCELLED' } }
try {
  Assert-NotCancelled
  $services=@(Get-CimInstance Win32_Service -ErrorAction Stop | Where-Object { $_.Name -in @('wiresock-client-service','wiresock-pro-client-service') })
  if($services | Where-Object { $_.State -ne 'Stopped' -or [int]$_.ProcessId -ne 0 }){ throw 'EXTERNAL_SERVICE_ACTIVE' }
  $processes=@(Get-CimInstance Win32_Process -Filter "Name='wiresock-client.exe'" -ErrorAction Stop)
  if($processes.Count -ne 0){ throw 'EXTERNAL_PROCESS_ACTIVE' }
  Assert-NotCancelled
  $arguments=@('run','-config',('"'+${literal(configPath)}+'"'),'-log-level','info','-network-lock','disabled')
  $child=Start-Process -FilePath ${literal(executable)} -ArgumentList $arguments -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru -ErrorAction Stop
  $handle=$child.Handle
  $deadline=(Get-Date).AddSeconds(3)
  do {
    if(Test-Path -LiteralPath $cancelPath){
      $child.Refresh()
      if(-not $child.HasExited){ Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
      throw 'CANCELLED_AFTER_START'
    }
    Start-Sleep -Milliseconds 100
    $child.Refresh()
    if($child.HasExited){ $child.WaitForExit(); throw "DIRECT_EXITED: $($child.ExitCode)" }
  } while((Get-Date) -lt $deadline)
  Complete 0 "DIRECT_RUNNING: pid=$($child.Id)"
} catch { Complete 1 ("DIRECT_REFUSED: "+$_.Exception.Message) }`;
}

function runPowerShell(args: string[]): Promise<void> {
  return new Promise((resolve, reject) => {
    execFile("powershell.exe", args, { windowsHide: true, timeout: 120_000 }, error => error ? reject(error) : resolve());
  });
}

export async function startNativeWireSock(configPath: string, rawConfig: string, allowedAppPaths: string[], signal?: AbortSignal) {
  if (process.platform !== "win32" || process.arch !== "x64") throw new Error("Windows x64 is required.");
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  const validation = validateWireGuardConfig(rawConfig);
  if (!validation.valid) throw new Error("The WireGuard profile is invalid.");
  const before = await inspectWireSockAsync(configPath);
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  if (!before.reliable) throw new Error("WireSock state is unreliable.");
  if (before.active && !before.owned) throw new Error("Another WireSock tunnel is active.");
  if (before.active && before.owned) return { configPath };
  const candidate = findWireSockCandidate();
  if (!candidate) throw new Error("A compatible WireSock SDK installation is required.");

  const target = path.resolve(configPath);
  const nonce = `${process.pid}.${Date.now()}`;
  const staging = `${target}.${nonce}.tmp`;
  const scriptPath = `${target}.${nonce}.ps1`;
  const resultPath = `${target}.${nonce}.result`;
  const cancelPath = `${target}.${nonce}.cancel`;
  let cancellationMarked = false;
  const markCancelled = () => {
    cancellationMarked = true;
    try { fs.writeFileSync(cancelPath, "cancelled", { encoding: "utf8", mode: 0o600, flag: "wx" }); } catch {}
  };
  fs.mkdirSync(path.dirname(target), { recursive: true });
  signal?.addEventListener("abort", markCancelled, { once: true });
  try {
    fs.writeFileSync(staging, sanitizeWireGuardConfig(rawConfig, formatAllowedApps(allowedAppPaths)), { encoding: "utf8", mode: 0o600, flag: "wx" });
    fs.rmSync(target, { force: true });
    fs.renameSync(staging, target);
    fs.writeFileSync(scriptPath, nativeDirectScript(candidate.executable, target, resultPath, cancelPath), { encoding: "utf8", mode: 0o600, flag: "wx" });
    if (signal?.aborted) markCancelled();
    try { await runPowerShell(elevatedPowerShellFileArgs(scriptPath, resultPath)); } catch { markCancelled(); }
    let result = "";
    try { result = fs.readFileSync(resultPath, "utf8"); } catch {}
    if (signal?.aborted) throw new Error("WireSock start was cancelled.");
    if (!/^0\r?\nDIRECT_RUNNING: pid=\d+\s*$/.test(result)) throw new Error("The elevated WireSock start was refused.");
  } finally {
    signal?.removeEventListener("abort", markCancelled);
    for (const file of [staging, scriptPath, resultPath, `${resultPath}.tmp`, cancelPath, `${resultPath}.stdout`, `${resultPath}.stderr`]) {
      if (file === cancelPath && cancellationMarked) continue; // A delayed UAC worker must still see cancellation.
      try { fs.rmSync(file, { force: true }); } catch {}
    }
  }
  const after = await inspectWireSockAsync(target);
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  if (!after.reliable || !after.active || !after.owned) throw new Error("WireSock did not confirm the exact native-owned config.");
  return { configPath: target };
}
