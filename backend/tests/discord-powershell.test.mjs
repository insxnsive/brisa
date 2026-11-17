import assert from "node:assert/strict";
import { execFile as execute, spawn as spawnProcess } from "node:child_process";
import { EventEmitter } from "node:events";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { createDiscordLifecycle } from "../src/discord-lifecycle.mjs";

const app = String.raw`C:\Fixture's Folder\Discord\app-1\Discord.exe`;
const literal = value => `'${value.replaceAll("'", "''")}'`;

test("real PowerShell preserves Discord ownership under an accented folder", { skip: process.platform !== "win32" }, async () => {
  // Windows can hand back an 8.3 temp path while PowerShell reports the long form, so compare on the long one.
  const root = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), "brisa-discord-encoding-")));
  try {
    const executable = path.join(root, "Jo\u00e3o", "Discord", "app-1", "Discord.exe");
    fs.mkdirSync(path.dirname(executable), { recursive: true });
    fs.writeFileSync(executable, "synthetic fixture");
    let reported;
    const execFile = (file, args, options, callback) => {
      const fixture = `function Get-Process { [CmdletBinding()] param($Name) [pscustomobject]@{Path=(Get-Item -LiteralPath ${literal(executable)}).FullName} }; function Get-CimInstance { [pscustomobject]@{ProcessId=42;ParentProcessId=1;Name='Discord.exe';ExecutablePath=(Get-Item -LiteralPath ${literal(executable)}).FullName} }; `;
      return execute(file, [...args.slice(0, -1), fixture + args.at(-1)], options, (error, stdout) => {
        if (!error) { const first = JSON.parse(stdout)[0]; reported = typeof first === "string" ? first : first.ExecutablePath; }
        callback(error, stdout);
      });
    };
    const lifecycle = createDiscordLifecycle({ execFile });
    assert.equal(await lifecycle.isRunning([executable]), true);
    assert.deepEqual(Buffer.from(reported, "utf8"), Buffer.from(executable, "utf8"));
    assert.deepEqual(await lifecycle.runningApps([executable]), [executable]);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

for (const changed of [false, true]) {
  test(`real PowerShell parsing ${changed ? "rejects a changed process identity" : "accepts quoted owned identity"}`, { skip: process.platform !== "win32" }, async () => {
    let active = true, commands = 0;
    const execFile = (file, args, options, callback) => {
      const script = args.at(-1);
      if (script.includes("ConvertTo-Json")) {
        callback(null, JSON.stringify(active ? [10, 11].map(ProcessId => ({ ProcessId, Name: "Discord.exe", ExecutablePath: app })) : []));
        return;
      }
      // Shadow both CIM commands inside this fresh PowerShell process. These
      // functions operate only on a synthetic object: no live inspection/kill.
      const prelude = `$global:fixtureTerminated=0; function Get-CimInstance { param($ClassName,$Filter) [pscustomobject]@{ExecutablePath=${literal(changed ? "C:\\Other\\Discord.exe" : app)}} }; function Invoke-CimMethod { param($InputObject,$MethodName) $global:fixtureTerminated++; [pscustomobject]@{ReturnValue=0} }; function Get-Process { param($Id) $p=[pscustomobject]@{Handle=1; HasExited=$false; MainModule=[pscustomobject]@{FileName=${literal(changed ? "C:\\Other\\Discord.exe" : app)}}}; $p | Add-Member ScriptMethod Kill {$global:fixtureTerminated++}; $p | Add-Member ScriptMethod WaitForExit {param($milliseconds) return $this.HasExited}; $p | Add-Member ScriptMethod Dispose {}; return $p }; `;
      const command = prelude + script + "; if($global:fixtureTerminated -ne 2){throw 'FIXTURE_NOT_TERMINATED'}";
      commands++;
      assert.ok(options.timeout >= 10_000, "cold PowerShell startup needs a bounded ten-second command deadline");
      execute(file, [...args.slice(0, -1), command], options, (error, stdout) => {
        if (!error) active = false;
        callback(error, stdout);
      });
    };
    const operation = createDiscordLifecycle({ execFile, sleep: async () => {}, maxAttempts: 4 }).stop([app]);
    if (changed) await assert.rejects(operation, /PROCESS_IDENTITY_CHANGED/);
    else await operation;
    assert.equal(commands, 1);
    assert.equal(active, changed);
  });
}

for (const scenario of [
  { name: "exits while its path is being read", pathThrows: true, exited: true, kills: 0 },
  { name: "exits between the path check and termination", killThrows: true, exited: true, kills: 1 },
  { name: "remains alive with an unreadable path", missingPath: true, exited: false, kills: 0, error: /PROCESS_IDENTITY_CHANGED/ },
  { name: "refuses termination while still alive", killThrows: true, exited: false, kills: 1, error: /FIXTURE_TERMINATION_DENIED/ },
]) {
  test(`handle-bound shutdown: child ${scenario.name}`, { skip: process.platform !== "win32" }, async () => {
    let active = true;
    const execFile = (file, args, options, callback) => {
      const script = args.at(-1);
      if (script.includes("ConvertTo-Json")) return callback(null, JSON.stringify(active ?
        [{ ProcessId: 10, Name: "Discord.exe", ExecutablePath: app }] : []));
      const pathProperty = scenario.pathThrows
        ? "$p | Add-Member ScriptProperty MainModule {throw 'FIXTURE_MODULE_GONE'};"
        : `$p | Add-Member NoteProperty MainModule ([pscustomobject]@{FileName=${scenario.missingPath ? "$null" : literal(app)}});`;
      const prelude = `$global:kills=0; $global:disposed=0; function Get-Process { param($Id) $p=[pscustomobject]@{Handle=1;HasExited=$false}; ${pathProperty} $p | Add-Member ScriptMethod Kill {$global:kills++; ${scenario.killThrows ? "throw 'FIXTURE_TERMINATION_DENIED'" : ""}}; $p | Add-Member ScriptMethod WaitForExit {param($milliseconds) return $${scenario.exited}}; $p | Add-Member ScriptMethod Dispose {$global:disposed++}; return $p }; function Get-CimInstance {throw 'UNEXPECTED_CIM'}; function Invoke-CimMethod {throw 'UNEXPECTED_CIM'}; `;
      const command = prelude + `try { & { ${script} } } catch { $failure=$_ }; if($global:kills -ne ${scenario.kills} -or $global:disposed -ne 1){throw 'HANDLE_OR_KILL_CONTRACT_FAILED'}; if($failure){throw $failure};`;
      execute(file, [...args.slice(0, -1), command], options, (error, stdout) => {
        if (!error) active = false;
        callback(error, stdout);
      });
    };
    const operation = createDiscordLifecycle({ execFile, sleep: async () => {}, maxAttempts: 3 }).stop([app]);
    if (scenario.error) await assert.rejects(operation, scenario.error);
    else await operation;
  });
}

test("a child that exits during parent shutdown is not an identity violation", { skip: process.platform !== "win32" }, async () => {
  let active = true;
  const execFile = (file, args, options, callback) => {
    const script = args.at(-1);
    if (script.includes("ConvertTo-Json")) return callback(null, JSON.stringify(active ?
      [{ ProcessId: 10, Name: "Discord.exe", ExecutablePath: app }] : []));
    // WMI can retain a terminating child row after its executable path disappears.
    // No live process API is allowed in this fixture.
    const prelude = `function Get-CimInstance { param($ClassName,$Filter) [pscustomobject]@{ExecutablePath=$null} }; function Get-Process { param($Id) return $null }; function Invoke-CimMethod { throw 'MUST_NOT_TERMINATE' }; `;
    execute(file, [...args.slice(0, -1), prelude + script], options, (error, stdout) => {
      if (!error) active = false;
      callback(error, stdout);
    });
  };
  await createDiscordLifecycle({ execFile, sleep: async () => {}, maxAttempts: 3 }).stop([app]);
  assert.equal(active, false);
});


function statusFixture(prelude, requested = app) {
  const execFile = (file, args, options, callback) => execute(file, [...args.slice(0, -1), prelude + args.at(-1)], options, callback);
  return createDiscordLifecycle({ execFile }).isRunning([requested]);
}

test("speed T1 real PowerShell reads only Path for named status", { skip: process.platform !== "win32" }, async () => {
  const prelude = `function Get-Process { [CmdletBinding()] param($Name) if($Name -ne 'discord'){throw 'BAD_NAME'}; $p=[pscustomobject]@{Path=${literal(app)}}; $p | Add-Member ScriptProperty ExecutablePath {throw 'MUST_NOT_READ'}; $p | Add-Member ScriptProperty MainWindowTitle {throw 'MUST_NOT_READ'}; $p }; function Get-CimInstance {throw 'MUST_NOT_USE_CIM'}; `;
  assert.equal(await statusFixture(prelude), true);
});

test("speed T1 real missing process is false and access failure is an error", { skip: process.platform !== "win32" }, async () => {
  assert.equal(await statusFixture("function Get-CimInstance {throw 'MUST_NOT_USE_CIM'}; ", String.raw`C:\fixture\brisa-definitely-not-a-process.exe`), false);
  await assert.rejects(statusFixture("function Get-Process { [CmdletBinding()] param($Name) Write-Error 'FIXTURE_QUERY_DENIED' }; "), /FIXTURE_QUERY_DENIED/);
});

for (const path of ["$null", "{throw 'FIXTURE_PATH_UNREADABLE'}"]) {
  test(`speed T1 unreadable Path cannot prove running: ${path}`, { skip: process.platform !== "win32" }, async () => {
    const member = path.startsWith("{") ? `ScriptProperty Path ${path}` : `NoteProperty Path ${path}`;
    assert.equal(await statusFixture(`function Get-Process { [CmdletBinding()] param($Name) $p=[pscustomobject]@{}; $p | Add-Member ${member}; $p }; function Get-CimInstance {throw 'MUST_NOT_USE_CIM'}; `), false);
  });
}


function spawnedChild() {
  const child = new EventEmitter(); child.unref = () => {};
  queueMicrotask(() => child.emit("spawn")); return child;
}

const forbidOneShot = (_file, _args, _options, callback) => callback(new Error("PERSISTENT_RUNNER_REQUIRED"));
function persistentFixture({ initial = [], polls = null, failKill = false, maxAttempts = 4, stableChecks = 2, malformed = false } = {}) {
  let sessions = 0, spawned = false, inspections = 0;
  const sessionSpawn = (file, args, options) => {
    sessions++;
    const rows = spawned && polls ? polls : null;
    const prelude = `$global:rows=ConvertFrom-Json ${literal(JSON.stringify(initial))}; $global:poll=0; $global:samples=ConvertFrom-Json ${literal(JSON.stringify(rows))};
function Get-CimInstance { [CmdletBinding()] param($ClassName) if($null -ne $global:samples){ $index=[Math]::Min($global:poll,$global:samples.Count-1); $global:poll++; @($global:samples[$index]) }else{@($global:rows)} }
function Get-Process { param($Id) $row=@($global:rows|Where-Object {$_.ProcessId -eq $Id})[0]; if(-not $row){return $null}; $p=[pscustomobject]@{Handle=1;HasExited=$false;MainModule=[pscustomobject]@{FileName=$row.ExecutablePath};FixtureId=$Id}; $p | Add-Member ScriptMethod Kill { ${failKill ? "throw 'FIXTURE_TERMINATION_DENIED'" : "$global:rows=@($global:rows|Where-Object {$_.ProcessId -ne $this.FixtureId})"} }; $p | Add-Member ScriptMethod WaitForExit {param($ms) return $false}; $p | Add-Member ScriptMethod Dispose {}; $p }
${malformed ? "[Console]::Out.WriteLine('malformed-frame')" : ""}
`;
    return spawnProcess(file, [...args.slice(0, -1), prelude + args.at(-1)], options);
  };
  const lifecycle = createDiscordLifecycle({ execFile: forbidOneShot, sessionSpawn, spawn: () => { spawned = true; return spawnedChild(); }, sleep: async () => { inspections++; }, maxAttempts, stableChecks });
  return { lifecycle, get sessions() { return sessions; }, get inspections() { return inspections; } };
}

test("speed N1 persistent stop uses one helper for checked kill and two empty polls", { skip: process.platform !== "win32" }, async () => {
  const f = persistentFixture({ initial: [{ ProcessId: 900001, ParentProcessId: 1, Name: "Discord.exe", ExecutablePath: app }] });
  await f.lifecycle.stop([app]);
  assert.equal(f.sessions, 1);
  assert.equal(f.inspections, 2, "kill poll then two empty reads");
});

test("speed N1 persistent stop refuses unreadable owned evidence", { skip: process.platform !== "win32" }, async () => {
  const f = persistentFixture({ initial: [{ ProcessId: 900001, Name: "Discord.exe", ExecutablePath: null }] });
  await assert.rejects(f.lifecycle.stop([app]), /ownership/); assert.equal(f.sessions, 1);
});

test("speed N1 persistent stop preserves checked termination failure", { skip: process.platform !== "win32" }, async () => {
  const f = persistentFixture({ initial: [{ ProcessId: 900001, Name: "Discord.exe", ExecutablePath: app }], failKill: true });
  await assert.rejects(f.lifecycle.stop([app]), /FIXTURE_TERMINATION_DENIED/);
});

test("speed N1 launch uses one prelaunch check and one persistent confirmation helper", { skip: process.platform !== "win32" }, async () => {
  const sibling = app.replace("app-1", "app-2");
  const row = { ProcessId: 900001, Name: "DISCORD.EXE", ExecutablePath: sibling.toUpperCase() };
  const f = persistentFixture({ polls: [[row], [row]], maxAttempts: 2 });
  await f.lifecycle.launch([app]); assert.equal(f.sessions, 2);
  assert.equal(f.inspections, 1, "one delay between two positive confirmation polls, none before spawn");
});

test("speed N1 persistent confirmation filters unreadable rows and resets stability", { skip: process.platform !== "win32" }, async () => {
  const readable = { ProcessId: 900001, Name: "Discord.exe", ExecutablePath: app.replace("app-1", "app-2") };
  const unreadable = { ProcessId: 900002, Name: "Discord.exe", ExecutablePath: null };
  const f = persistentFixture({ polls: [[readable, unreadable], [], [readable], [readable]], maxAttempts: 4 });
  await f.lifecycle.launch([app]); assert.equal(f.sessions, 2); assert.equal(f.inspections, 3);
});

test("speed N1 persistent helper rejects malformed transport instead of proving absence", { skip: process.platform !== "win32" }, async () => {
  const f = persistentFixture({ malformed: true });
  await assert.rejects(f.lifecycle.stop([app]), /response.*unreadable/);
});

test("speed N1 persistent helper preserves spawn failure code", async () => {
  const lifecycle = createDiscordLifecycle({ execFile: forbidOneShot, sessionSpawn: (_file, args, options) => spawnProcess("brisa-missing-helper-fixture.exe", args, options) });
  await assert.rejects(lifecycle.stop([app]), error => error.code === "ENOENT");
});

test("speed N1 persistent confirmation cancellation preserves AbortError", { skip: process.platform !== "win32" }, async () => {
  const controller = new AbortController(); let spawned = false;
  const f = persistentFixture({ polls: [[], []], maxAttempts: 4 });
  const sessionSpawn = (file, args, options) => spawnProcess(file, [...args.slice(0, -1), "function Get-CimInstance {param($ClassName)}; " + args.at(-1)], options);
  const lifecycle = createDiscordLifecycle({ execFile: forbidOneShot, sessionSpawn, spawn: () => { spawned = true; return spawnedChild(); }, sleep: async () => { if (spawned) controller.abort(); } });
  await assert.rejects(lifecycle.launch([app], controller.signal), error => error.name === "AbortError" && error.code === "ABORT_ERR");
});
