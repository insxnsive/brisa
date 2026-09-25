import { execFile as nodeExecFile, spawn as nodeSpawn } from "node:child_process";
import path from "node:path";

const win = path.win32;
const INSPECT_SCRIPT = "$ErrorActionPreference='Stop'; ConvertTo-Json -InputObject @(Get-CimInstance Win32_Process -ErrorAction Stop | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath) -Compress";
// Hold the OS process handle while checking its path and terminating it. WMI
// can retain a dying Electron child with no ExecutablePath after the parent exits.
// A process that actually exited is success; an unreadable/live or replaced one
// still fails closed. Never fall back to a name-wide taskkill or a process tree.
const KILL_SCRIPT = `param([int]$TargetPid,[string]$ExpectedPath)
$ErrorActionPreference='Stop'; $p=$null;
try {
  $p=Get-Process -Id $TargetPid -ErrorAction SilentlyContinue;
  if($null -eq $p){return};
  $null=$p.Handle;
  if($p.HasExited){return};
  $actual=$p.MainModule.FileName;
  if(-not $actual -or $actual -ine $ExpectedPath){throw 'PROCESS_IDENTITY_CHANGED'};
  $p.Kill();
} catch {
  if($null -eq $p -or -not $p.WaitForExit(250)){throw};
} finally {
  if($null -ne $p){$p.Dispose()};
}`;
const psLiteral = value => `'${value.replaceAll("'", "''")}'`;

const key = value => value.toLowerCase();
const abortError = () => Object.assign(new Error("Operation cancelled."), { name: "AbortError", code: "ABORT_ERR" });
const checkAbort = signal => { if (signal?.aborted) throw abortError(); };

function validateApps(apps) {
  if (!Array.isArray(apps) || apps.length === 0) throw new TypeError("At least one Discord executable is required.");
  const unique = new Map();
  for (const value of apps) {
    if (typeof value !== "string" || !win.isAbsolute(value) || value.includes("\0") || value.includes("\r") || value.includes("\n")) {
      throw new TypeError("Discord executable paths must be absolute Windows paths.");
    }
    const normalized = win.normalize(value);
    if (win.extname(normalized).toLowerCase() !== ".exe") throw new TypeError("The requested Discord application must be an executable.");
    if (!unique.has(key(normalized))) unique.set(key(normalized), normalized);
  }
  return [...unique.values()];
}

export function discordAllowedApps(apps) {
  const executables = validateApps(apps);
  const appDirs = executables.map(win.dirname);
  const roots = appDirs.map(dir => /^app-[^\\/]+$/i.test(win.basename(dir)) ? win.dirname(dir) : dir);
  return {
    executables,
    executableNames: [...new Map(executables.map(value => [key(win.basename(value)), win.basename(value)])).values()],
    appDirs: [...new Map(appDirs.map(value => [key(value), value])).values()],
    updaterPaths: [...new Map(roots.map(root => [key(win.join(root, "Update.exe")), win.join(root, "Update.exe")])).values()],
  };
}

export function createDiscordLifecycle(options = {}) {
  const execFile = options.execFile ?? nodeExecFile;
  const spawn = options.spawn ?? nodeSpawn;
  const sleep = options.sleep ?? (ms => new Promise(resolve => setTimeout(resolve, ms)));
  const clock = options.clock ?? (() => Date.now());
  const maxAttempts = Math.max(1, options.maxAttempts ?? Number.POSITIVE_INFINITY);
  const stableChecks = Math.max(1, options.stableChecks ?? 2);
  const pollIntervalMs = Math.max(0, options.pollIntervalMs ?? 100);
  const powershell = options.powershell ?? "powershell.exe";

  const run = (script, extra = [], signal) => new Promise((resolve, reject) => {
    execFile(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", extra.length ? `& { ${script} } ${extra.join(" ")}` : script],
      { windowsHide: true, encoding: "utf8", maxBuffer: 1024 * 1024, timeout: 5_000, signal },
      (error, stdout) => error ? reject(error) : resolve(stdout));
  });

  const inspect = async signal => {
    checkAbort(signal);
    const raw = await run(INSPECT_SCRIPT, [], signal);
    checkAbort(signal);
    if (!raw.trim()) throw new Error("Discord process inspection returned no evidence.");
    let parsed;
    try { parsed = JSON.parse(raw); } catch { throw new Error("Discord process inspection returned unreadable data."); }
    const rows = Array.isArray(parsed) ? parsed : [parsed];
    return rows.map(row => ({ pid: Number(row?.ProcessId), parentPid: Number(row?.ParentProcessId), name: typeof row?.Name === "string" ? row.Name : "", executablePath: typeof row?.ExecutablePath === "string" && row.ExecutablePath ? win.normalize(row.ExecutablePath) : null }))
      .filter(row => Number.isSafeInteger(row.pid) && row.pid > 0);
  };

  const ownership = (apps, processes) => {
    const allowed = discordAllowedApps(apps);
    const exact = new Set(allowed.executables.map(key));
    const updaters = new Set(allowed.updaterPaths.map(key));
    const roots = allowed.updaterPaths.map(win.dirname);
    const appNames = new Set(allowed.executables.map(value => key(win.basename(value))));
    const knownPids = new Set(processes.filter(p => p.executablePath && (exact.has(key(p.executablePath)) || updaters.has(key(p.executablePath)))).map(p => p.pid));
    for (const process of processes) {
      // Update.exe is shared by unrelated Squirrel apps. Its name is never
      // ownership evidence; an unreadable child of an owned client is different.
      if (!process.executablePath && (appNames.has(process.name.toLowerCase()) ||
          process.name.toLowerCase() === "update.exe" && knownPids.has(process.parentPid))) {
        throw new Error("Discord process ownership could not be verified.");
      }
    }
    const owned = processes.filter(process => {
      if (!process.executablePath) return false;
      const processKey = key(process.executablePath);
      if (exact.has(processKey) || updaters.has(processKey)) return true;
      if (!appNames.has(process.name.toLowerCase()) || !appNames.has(key(win.basename(process.executablePath)))) return false;
      return roots.some(root => {
        const relative = win.relative(root, process.executablePath);
        return relative && !relative.startsWith("..") && !win.isAbsolute(relative) && /^app-[^\\/]+$/i.test(win.dirname(relative));
      });
    });
    return { allowed, owned };
  };

  const killOwned = async (candidates, signal) => {
    // One PowerShell startup per batch; every PID/path is still re-read at the
    // actual termination point. Missing children may already have exited.
    const commands = candidates.map(candidate =>
      `& { ${KILL_SCRIPT} } -TargetPid ${candidate.pid} -ExpectedPath ${psLiteral(candidate.executablePath)}`);
    await run(`$ErrorActionPreference='Stop'; ${commands.join("; ")}`, [], signal);
  };

  const stop = async (apps, signal) => {
    validateApps(apps);
    let stable = 0;
    const deadline = clock() + 12_000;
    for (let attempt = 0; attempt < maxAttempts && clock() < deadline; attempt += 1) {
      checkAbort(signal);
      const { owned } = ownership(apps, await inspect(signal));
      if (owned.length === 0) {
        stable += 1;
        if (stable >= stableChecks) return;
      } else {
        stable = 0;
        await killOwned(owned, signal);
      }
      if (attempt + 1 < maxAttempts) await sleep(pollIntervalMs, signal);
    }
    throw new Error("Discord processes did not stop within the bounded wait.");
  };

  const runningApps = async (apps, signal) => {
    const requested = validateApps(apps);
    const processes = await inspect(signal);
    return requested.filter(app => ownership([app], processes).owned.some(p =>
      key(win.basename(p.executablePath)) === key(win.basename(app))));
  };

  const isRunning = async (apps, signal) => {
    const requested = validateApps(apps);
    const processes = ownership(apps, await inspect(signal)).owned;
    return requested.every(app => processes.some(p => key(p.executablePath) === key(app)));
  };

  const waitForMain = async (app, signal) => {
    const deadline = clock() + 12_000;
    for (let attempt = 0; attempt < maxAttempts && clock() < deadline; attempt += 1) {
      checkAbort(signal);
      const processes = await inspect(signal);
      if (processes.some(process => process.executablePath && key(process.executablePath) === key(win.normalize(app)))) return;
      if (attempt + 1 < maxAttempts) await sleep(pollIntervalMs, signal);
    }
    throw new Error(`Discord did not start within the bounded wait (${clock()}).`);
  };

  const launch = async (apps, signal) => {
    const requested = validateApps(apps);
    checkAbort(signal);
    await stop(requested, signal);
    for (const app of requested) {
      checkAbort(signal);
      await new Promise((resolve, reject) => {
        let settled = false;
        // The desktop shell launches Discord independently of the hidden Node
        // console/job. Restoring a client must survive the backend exiting.
        const child = spawn("explorer.exe", [app], { stdio: "ignore", windowsHide: true });
        const done = fn => value => { if (!settled) { settled = true; fn(value); } };
        child.once("error", done(reject));
        child.once("spawn", done(() => { child.unref?.(); resolve(); }));
      });
      await waitForMain(app, signal);
    }
  };

  return { stop, launch, isRunning, runningApps };
}
