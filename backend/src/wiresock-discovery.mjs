import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

// Read only installation metadata. Do not return service arguments or profiles.
// Only machine-scope sources are read: HKCU and the running process path can both be written by
// a non-elevated user, and whatever this finds gets launched elevated.
export const WIRESOCK_LOCATION_SCRIPT = String.raw`
$ErrorActionPreference='Stop'
$locations=@()
$keys=@('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*')
foreach($key in $keys){
 Get-ItemProperty -Path $key -ErrorAction SilentlyContinue | ForEach-Object {
  if(($_.DisplayName -match 'WireSock|NTKERNEL[.]WireSock') -and $_.InstallLocation){
   $locations += [Environment]::ExpandEnvironmentVariables([string]$_.InstallLocation)
  }
 }
}
Get-CimInstance Win32_Service -Filter "Name LIKE '%wiresock%'" -ErrorAction SilentlyContinue | ForEach-Object {
 if($_.PathName -match '^\s*(?:"([^"]*\\wiresock-client\.exe)"|(.+?\\wiresock-client\.exe))(?=\s|$)'){
  $exe=if($Matches[1]){$Matches[1]}else{$Matches[2]}
  $locations += [IO.Path]::GetDirectoryName($exe)
 }
}
ConvertTo-Json -InputObject @($locations | Select-Object -Unique) -Compress
`;

export function readWireSockInstallLocations(run = execFileSync) {
  if (process.platform !== "win32") return [];
  try {
    const output = run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", WIRESOCK_LOCATION_SCRIPT], {
      encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: 8000,
    });
    const value = JSON.parse(output.trim());
    return Array.isArray(value) ? value : [];
  } catch { return []; }
}

export function wireSockSearchRoots(env = process.env, installed = readWireSockInstallLocations()) {
  const defaults = [env.ProgramW6432, env.ProgramFiles, env["ProgramFiles(x86)"], "C:\\Program Files"]
    .filter(Boolean).map(directory => path.win32.join(directory, "WireSock Secure Connect"));
  if (env.LOCALAPPDATA) defaults.push(path.win32.join(env.LOCALAPPDATA, "Microsoft", "WinGet", "Packages"));
  const roots = new Map();
  for (const value of [...installed, ...defaults]) {
    // Registry metadata is untrusted: accept local absolute directories only.
    if (typeof value !== "string" || value.length > 2048 || /[\x00-\x1f"<>|?*]/.test(value) || !/^[a-z]:[\\/]/i.test(value)) continue;
    const root = path.win32.normalize(value);
    if (!roots.has(root.toLowerCase())) roots.set(root.toLowerCase(), root);
  }
  return [...roots.values()];
}

export function wireSockCandidatePairs(roots, files = fs) {
  const pairs = [];
  const seen = new Set();
  const add = root => {
    const executable = path.win32.join(root, "wiresock-client.exe");
    const booster = path.win32.join(root, "wgbooster.dll");
    const key = executable.toLowerCase();
    if (!seen.has(key) && files.existsSync(executable) && files.existsSync(booster)) {
      seen.add(key);
      pairs.push({ executable, booster });
    }
  };
  for (const root of roots) {
    add(root);
    add(path.win32.join(root, "sdk"));
    if (!/Packages$/i.test(root)) continue;
    try {
      for (const entry of files.readdirSync(root, { withFileTypes: true })) {
        if (!entry.isDirectory() || !/wiresock|ntkernel\.wiresock/i.test(entry.name)) continue;
        const packageRoot = path.win32.join(root, entry.name);
        for (const layout of [packageRoot, path.win32.join(packageRoot, "x64")]) {
          add(layout);
          add(path.win32.join(layout, "sdk"));
        }
      }
    } catch {}
  }
  return pairs;
}
