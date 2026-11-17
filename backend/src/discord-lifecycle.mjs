import { execFile as nodeExecFile, spawn as nodeSpawn } from "node:child_process";
import path from "node:path";
import { powerShellCommandArgs } from "./network/powershell.ts";

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

// Status needs only readable paths of named clients; it never authorizes termination.
function runningPathsScript(apps) {
  const names = [...new Set(apps.map(app => key(win.basename(app).slice(0, -4))))];
  return `$ErrorActionPreference='Stop'; $paths=@()
foreach($n in @(${names.map(psLiteral).join(",")})){
  try { $found=@(Get-Process -Name $n -ErrorAction Stop) }
  catch {
    $missing=($_.FullyQualifiedErrorId.Split(',')[0] -eq 'NoProcessFoundForGivenName') -and
      ($_.Exception.GetType().FullName -eq 'Microsoft.PowerShell.Commands.ProcessCommandException') -and
      ($_.CategoryInfo.Category -eq [Management.Automation.ErrorCategory]::ObjectNotFound) -and
      ([string]$_.TargetObject -eq $n)
    if($missing){continue}; throw
  }
  foreach($p in $found){
    try { $actual=$p.Path; if(-not [string]::IsNullOrWhiteSpace($actual)){ $paths += [string]$actual } } catch {}
  }
}
ConvertTo-Json -InputObject @($paths) -Compress`;
}

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


// Reuse one helper for a bounded lifecycle loop; JS still owns all identity decisions.
const SESSION_SCRIPT = `$ErrorActionPreference='Stop'
while($null -ne ($line=[Console]::In.ReadLine())){
  try {
    $request=$line|ConvertFrom-Json
    $output=(& ([scriptblock]::Create([string]$request.script)) | Out-String -Width 4096)
    $response=@{id=$request.id;ok=$true;stdout=[string]$output}
  } catch { $response=@{id=$request.id;ok=$false;message=[string]$_.Exception.Message} }
  [Console]::Out.WriteLine(($response|ConvertTo-Json -Compress -Depth 4))
}`;

function createPowerShellSession(spawn, powershell, signal) {
  checkAbort(signal);
  const child = spawn(powershell, ["-NoLogo", ...powerShellCommandArgs(SESSION_SCRIPT)],
    { windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
  let pending = null, sequence = 0, buffer = "", closed = false, lastError = null;
  const stopChild = () => { try { child.stdin.end(); } catch {} try { child.kill(); } catch {} };
  const fail = error => {
    if (closed) return;
    closed = true; lastError = error;
    if (pending) { clearTimeout(pending.timer); pending.reject(error); pending = null; }
    stopChild();
  };
  const abort = () => fail(abortError());
  child.once("error", fail);
  child.stdin.on("error", fail);
  child.stderr.on("data", () => {});
  child.stderr.on("error", fail);
  child.stdout.on("error", fail);
  child.stdout.setEncoding("utf8");
  child.stdout.on("data", chunk => {
    if (closed) return;
    buffer += chunk;
    if (Buffer.byteLength(buffer, "utf8") > 1024 * 1024) return fail(new Error("Discord process response exceeded its bounded size."));
    let end;
    while ((end = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, end).trim(); buffer = buffer.slice(end + 1);
      if (!line) continue;
      let response;
      try { response = JSON.parse(line); } catch { return fail(new Error("Discord process response was unreadable.")); }
      if (!pending || response?.id !== pending.id || typeof response.ok !== "boolean")
        return fail(new Error("Discord process response did not match its request."));
      const current = pending; pending = null; clearTimeout(current.timer);
      if (!response.ok) current.reject(Object.assign(new Error(typeof response.message === "string" ? response.message.slice(0, 4096) : "Discord process command failed."), { code: 1 }));
      else if (typeof response.stdout !== "string") { current.reject(new Error("Discord process response was unreadable.")); fail(new Error("Discord process response was unreadable.")); }
      else current.resolve(response.stdout);
    }
  });
  child.once("close", code => fail(Object.assign(new Error("Discord process helper exited before completion."), { code })));
  signal?.addEventListener("abort", abort, { once: true });
  if (signal?.aborted) abort();
  return {
    run: script => new Promise((resolve, reject) => {
      if (closed) return reject(lastError ?? new Error("Discord process helper is closed."));
      if (pending) return reject(new Error("Discord process helper already has a request."));
      const id = ++sequence;
      const timer = setTimeout(() => fail(new Error("Discord process command timed out.")), 10_000);
      pending = { id, resolve, reject, timer };
      try { child.stdin.write(JSON.stringify({ id, script }) + "\n"); } catch (error) { fail(error); }
    }),
    close: () => {
      signal?.removeEventListener("abort", abort);
      if (!closed) fail(new Error("Discord process helper is closed."));
    },
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
    execFile(powershell, ["-NoLogo", ...powerShellCommandArgs(extra.length ? `& { ${script} } ${extra.join(" ")}` : script)],
      { windowsHide: true, encoding: "utf8", maxBuffer: 1024 * 1024, timeout: 10_000, signal },
      (error, stdout) => error ? reject(error) : resolve(stdout));
  });

  const withSession = async (signal, operation) => {
    // An explicit execFile dependency remains a complete runner override. The sessionSpawn
    // seam lets tests exercise the production transport with synthetic PowerShell cmdlets.
    if (options.execFile && !options.sessionSpawn) return operation(run);
    const session = createPowerShellSession(options.sessionSpawn ?? nodeSpawn, powershell, signal);
    try { return await operation((script, _extra = [], _signal) => session.run(script)); }
    finally { session.close(); }
  };

  const inspect = async (signal, runner = run) => {
    checkAbort(signal);
    const raw = await runner(INSPECT_SCRIPT, [], signal);
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

  const killOwned = async (candidates, signal, runner = run) => {
    // One PowerShell startup per batch; every PID/path is still re-read at the
    // actual termination point. Missing children may already have exited.
    const commands = candidates.map(candidate =>
      `& { ${KILL_SCRIPT} } -TargetPid ${candidate.pid} -ExpectedPath ${psLiteral(candidate.executablePath)}`);
    await runner(`$ErrorActionPreference='Stop'; ${commands.join("; ")}`, [], signal);
  };

  const stopWithChecks = async (apps, signal, requiredChecks) => {
    validateApps(apps);
    return withSession(signal, async runner => {
    let stable = 0;
    const deadline = clock() + 12_000;
    for (let attempt = 0; attempt < maxAttempts && clock() < deadline; attempt += 1) {
      checkAbort(signal);
      const { owned } = ownership(apps, await inspect(signal, runner));
      if (owned.length === 0) {
        stable += 1;
        if (stable >= requiredChecks) return;
      } else {
        stable = 0;
        await killOwned(owned, signal, runner);
      }
      if (attempt + 1 < maxAttempts) await sleep(pollIntervalMs, signal);
    }
    throw new Error("Discord processes did not stop within the bounded wait.");
    });
  };

  const stop = (apps, signal) => stopWithChecks(apps, signal, stableChecks);

  const runningApps = async (apps, signal) => {
    const requested = validateApps(apps);
    const processes = await inspect(signal);
    return requested.filter(app => ownership([app], processes).owned.some(p =>
      key(win.basename(p.executablePath)) === key(win.basename(app))));
  };

  const isRunning = async (apps, signal) => {
    const requested = validateApps(apps);
    checkAbort(signal);
    const raw = await run(runningPathsScript(requested), [], signal);
    checkAbort(signal);
    if (!raw.trim()) throw new Error("Discord process inspection returned no evidence.");
    let parsed;
    try { parsed = JSON.parse(raw); } catch { throw new Error("Discord process inspection returned unreadable data."); }
    if (!Array.isArray(parsed) || parsed.some(value => typeof value !== "string"))
      throw new Error("Discord process inspection returned unreadable data.");
    const paths = new Set(parsed.filter(value => win.isAbsolute(value)).map(value => key(win.normalize(value))));
    return requested.every(app => paths.has(key(app)));
  };

  const waitForMain = async (app, signal) => withSession(signal, async runner => {
    const deadline = clock() + 12_000;
    let consecutive = 0;
    for (let attempt = 0; attempt < maxAttempts && clock() < deadline; attempt += 1) {
      checkAbort(signal);
      const rows = await inspect(signal, runner);
      const processes = ownership([app], rows.filter(row => row.executablePath)).owned;
      const running = processes.some(process => key(win.basename(process.executablePath)) === key(win.basename(app)));
      consecutive = running ? consecutive + 1 : 0;
      if (consecutive >= stableChecks) return;
      if (attempt + 1 < maxAttempts) await sleep(pollIntervalMs, signal);
    }
    throw new Error("Discord did not start within the bounded wait.");
  });

  const launch = async (apps, signal) => {
    const requested = validateApps(apps);
    checkAbort(signal);
    // Launch only needs a fresh empty check; explicit stop still requires two by default.
    await stopWithChecks(requested, signal, 1);
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
