import { execFile } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import { elevatedPowerShellFileArgs } from "./network/wiresock-service.ts";
import { quotePowerShell } from "./network/powershell.ts";
import { WireSockSetupError } from "./wiresock-setup.mjs";
import { classifyStartResult, WireSockStartError } from "./wiresock-start-result.mjs";
import { wireSockSearchRoots } from "./wiresock-discovery.mjs";
import { ensureWireSockCandidate, findWireSockCandidate, inspectWireSockAsync, learnCandidateLease, type WireSockCandidateLease } from "./network/vpn-windows.ts";
import { formatAllowedApps, sanitizeWireGuardConfig, validateWireGuardConfig } from "./network/vpn-types.ts";

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

type ExecFailure = { exitCode?: number; timedOut?: boolean };

function runPowerShell(args: string[]): Promise<void> {
  return new Promise((resolve, reject) => {
    execFile("powershell.exe", args, { windowsHide: true, timeout: 120_000 }, error => {
      if (!error) return resolve();
      // Only the exit status and timeout flag; the wrapper's stderr is vendor prose and
      // may carry paths, so it is never carried past this point.
      const failure: ExecFailure = {};
      const code = (error as { code?: unknown }).code;
      if (typeof code === "number") failure.exitCode = code;
      if ((error as { killed?: boolean }).killed === true) failure.timedOut = true;
      reject(failure);
    });
  });
}

export async function startNativeWireSock(configPath: string, rawConfig: string, allowedAppPaths: string[], signal?: AbortSignal, options?: { onStage?: (stage: string) => void; onEvent?: (code: string, data?: Record<string, number | boolean>) => void; onLease?: (lease: WireSockCandidateLease) => void }) {
  if (process.platform !== "win32" || process.arch !== "x64") throw new Error("Windows x64 is required.");
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  const validation = validateWireGuardConfig(rawConfig);
  if (!validation.valid) throw new Error("The WireGuard profile is invalid.");
  const before = await inspectWireSockAsync(configPath);
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  if (!before.reliable) throw new WireSockStartError("START_STATE_UNRELIABLE");
  if (before.active && !before.owned) throw new WireSockStartError("START_EXTERNAL_ACTIVE");
  if (before.active && before.owned) return { configPath };
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  // Um único preparo do SDK devolve o par inteiro; descobrir de novo aqui só repetia o
  // registry/CIM e podia escolher um par diferente do que acabou de ser validado.
  options?.onStage?.("installing-wiresock");
  const candidate = await ensureWireSockCandidate(() => {}, { signal, onEvent: options?.onEvent });
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  // A linha de base é aprendida agora, junto da seleção e antes do lançamento: aprender depois
  // carimbaria como confiável um par que já pudesse ter sido trocado durante o start.
  const lease = await learnCandidateLease(candidate, signal);
  if (lease) options?.onLease?.(lease);
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  // Revalidar imediatamente antes do uso elevado: a seleção e o lançamento estão separados por
  // uma janela em que o par poderia ser trocado, e o lease sozinho não prova identidade.
  // Revalidar imediatamente antes do uso elevado: a seleção e o lançamento estão separados
  // por uma janela em que o par poderia ser trocado, e o lease sozinho não prova identidade.
  // Uma sondagem assíncrona, não uma checagem por arquivo.
  const fresh = await findWireSockCandidate(process.env, { signal });
  if (!fresh || fresh.executable !== candidate.executable || fresh.executableVersion !== candidate.executableVersion) throw new WireSockStartError("START_NOT_APPROVED");
  options?.onStage?.("starting-tunnel");

  const target = path.resolve(configPath);
  const nonce = `${process.pid}.${Date.now()}`;
  const staging = `${target}.${nonce}.tmp`;
  const scriptPath = `${target}.${nonce}.ps1`;
  const resultPath = `${target}.${nonce}.result`;
  const cancelPath = `${target}.${nonce}.cancel`;
  let cancellationMarked = false;
  let execFailure: ExecFailure | null = null;
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
    try { await runPowerShell(elevatedPowerShellFileArgs(scriptPath, resultPath)); }
    catch (failure) { execFailure = failure as ExecFailure; markCancelled(); }
    let result = "";
    try { result = fs.readFileSync(resultPath, "utf8"); } catch {}
    if (signal?.aborted) throw new Error("WireSock start was cancelled.");
    const verdict = classifyStartResult(result, execFailure);
    if (!verdict.ok) throw new WireSockStartError(verdict.code, verdict.data);
  } finally {
    signal?.removeEventListener("abort", markCancelled);
    for (const file of [staging, scriptPath, resultPath, `${resultPath}.tmp`, cancelPath, `${resultPath}.stdout`, `${resultPath}.stderr`]) {
      if (file === cancelPath && cancellationMarked) continue; // A delayed UAC worker must still see cancellation.
      try { fs.rmSync(file, { force: true }); } catch {}
    }
  }
  const after = await inspectWireSockAsync(target);
  if (signal?.aborted) throw new Error("WireSock start was cancelled.");
  if (!after.reliable) throw new WireSockStartError("START_NOT_CONFIRMED", { reliable: false });
  if (!after.active || !after.owned) throw new WireSockStartError("START_NOT_CONFIRMED", { active: after.active === true, owned: after.owned === true });
  return { configPath: target, candidate };
}
