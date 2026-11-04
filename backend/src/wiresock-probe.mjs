import { execFile } from "node:child_process";

import { wireSockTrustHelpers } from "./wiresock-trust.mjs";

/**
 * Uma única sondagem assíncrona devolve tudo que a seleção precisa: as pastas encontradas, o par
 * EXE+booster de cada uma, as duas versões e o veredito de confiança. Antes disto a descoberta
 * disparava 2N+2 processos PowerShell síncronos no event loop, cada um com uma consulta de
 * registro/CIM ou uma verificação de assinatura.
 */
export function wireSockProbeScript(env = process.env) {
  const defaults = [env.ProgramW6432, env.ProgramFiles, env["ProgramFiles(x86)"], "C:\\Program Files"]
    .filter(value => typeof value === "string" && value)
    .map(value => `${value}\\WireSock Secure Connect`);
  const winGet = env.ProgramFiles ? `${env.ProgramFiles}\\WinGet\\Packages` : "";
  const literals = [...defaults, ...(winGet ? [winGet] : [])].map(value => `'${value.replace(/'/g, "''")}'`).join(",");
  return `$ErrorActionPreference='Stop'
$locations=@()
$keys=@('HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*',
 'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*')
foreach($key in $keys){
 Get-ItemProperty -Path $key -ErrorAction SilentlyContinue | ForEach-Object {
  if(($_.DisplayName -match 'WireSock|NTKERNEL[.]WireSock') -and $_.InstallLocation){
   $locations += [Environment]::ExpandEnvironmentVariables([string]$_.InstallLocation)
  }
 }
}
Get-CimInstance Win32_Service -Filter "Name LIKE '%wiresock%'" -ErrorAction SilentlyContinue | ForEach-Object {
 if($_.PathName -match '^\\s*(?:"([^"]*\\\\wiresock-client\\.exe)"|(.+?\\\\wiresock-client\\.exe))(?=\\s|$)'){
  $exe=if($Matches[1]){$Matches[1]}else{$Matches[2]}
  $locations += [IO.Path]::GetDirectoryName($exe)
 }
}
$locations += @(${literals})
$pairs=@()
$seen=@{}
function Add-BrisaPair([string]$root){
  foreach ($layout in @($root, (Join-Path $root 'sdk'))) {
    $exe = Join-Path $layout 'wiresock-client.exe'
    $dll = Join-Path $layout 'wgbooster.dll'
    if ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath $dll)) {
      $key = $exe.ToLowerInvariant()
      if (-not $seen.ContainsKey($key)) { $seen[$key] = $true; $script:pairs += [pscustomobject]@{ executable = $exe; booster = $dll } }
    }
  }
}
foreach ($root in $locations) {
  if ([string]::IsNullOrWhiteSpace($root)) { continue }
  if (-not ([IO.Path]::IsPathRooted($root))) { continue }
  Add-BrisaPair $root
  if ($root -notmatch 'Packages$') { continue }
  foreach ($dir in (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'wiresock|ntkernel\\.wiresock' })) {
    Add-BrisaPair $dir.FullName
    Add-BrisaPair (Join-Path $dir.FullName 'x64')
  }
}
${wireSockTrustHelpers()}
$out=@()
foreach ($pair in $pairs) {
  $executableVersion = ''
  $boosterVersion = ''
  try { $executableVersion = [string](Get-Item -LiteralPath $pair.executable).VersionInfo.FileVersion } catch { $executableVersion = '' }
  try { $boosterVersion = [string](Get-Item -LiteralPath $pair.booster).VersionInfo.FileVersion } catch { $boosterVersion = '' }
  $trusted = (Test-BrisaSignature $pair.executable) -and (Test-BrisaSignature $pair.booster) -and (Test-BrisaAcl $pair.executable) -and (Test-BrisaAcl $pair.booster) -and (Test-BrisaAclChain $pair.executable)
  $out += [pscustomobject]@{ executable = $pair.executable; booster = $pair.booster; executableVersion = $executableVersion; boosterVersion = $boosterVersion; trusted = [bool]$trusted }
}
ConvertTo-Json -InputObject @{ candidates = @($out) } -Compress -Depth 4`;
}

const VERSION = /^\d+\.\d+\.\d+\.\d+$/;

/**
 * O JSON vem de um processo que lê metadados de instalação, então nada nele é confiável: cada
 * campo é validado e uma entrada malformada é descartada, não corrigida. Saída ilegível devolve
 * null, e o chamador trata isso como "nenhum candidato confiável".
 */
export function parseWireSockProbe(stdout) {
  let value;
  try {
    value = JSON.parse(String(stdout ?? "").trim());
  } catch {
    return null;
  }
  if (!value || typeof value !== "object" || !Array.isArray(value.candidates)) return null;
  const candidates = [];
  for (const entry of value.candidates) {
    if (!entry || typeof entry !== "object") continue;
    const { executable, booster, executableVersion, boosterVersion, trusted } = entry;
    if (typeof executable !== "string" || executable.length > 2048 || !/^[a-z]:[\\/]/i.test(executable)) continue;
    if (typeof booster !== "string" || booster.length > 2048 || !/^[a-z]:[\\/]/i.test(booster)) continue;
    if (typeof executableVersion !== "string" || !VERSION.test(executableVersion)) continue;
    if (typeof boosterVersion !== "string" || !VERSION.test(boosterVersion)) continue;
    if (typeof trusted !== "boolean") continue;
    candidates.push({ executable, booster, executableVersion, boosterVersion, trusted });
  }
  return { candidates };
}

/** Runner padrão: um processo, com timeout e aborto cooperativo. */
export function runProbe(script, timeoutMs, signal) {
  return new Promise((resolve, reject) => {
    execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
      encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: timeoutMs, signal,
    }, (error, stdout) => (error ? reject(error) : resolve(stdout)));
  });
}

/**
 * Falha fechada em todos os caminhos: timeout, aborto, saída inválida ou ausência do campo de
 * confiança significam nenhum candidato confiável, nunca um candidato sem verificação.
 */
export async function probeWireSock(env = process.env, run = runProbe, timeoutMs = 15000, signal) {
  if (process.platform !== "win32") return { candidates: [], parsed: true };
  try {
    const stdout = await run(wireSockProbeScript(env), timeoutMs, signal);
    const parsed = parseWireSockProbe(stdout);
    return parsed ? { ...parsed, parsed: true } : { candidates: [], parsed: false };
  } catch {
    return { candidates: [], parsed: false };
  }
}
