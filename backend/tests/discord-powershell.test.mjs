import assert from "node:assert/strict";
import { execFile as execute } from "node:child_process";
import test from "node:test";
import { createDiscordLifecycle } from "../src/discord-lifecycle.mjs";

const app = String.raw`C:\Fixture's Folder\Discord\app-1\Discord.exe`;
const literal = value => `'${value.replaceAll("'", "''")}'`;
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
