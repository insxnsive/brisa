import { build, transformSync } from "esbuild";
import assert from "node:assert/strict";
import childProcess from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import { syncBuiltinESMExports } from "node:module";
import path from "node:path";
import { mock, test } from "node:test";
import workerThreads from "node:worker_threads";
import { fileURLToPath } from "node:url";
import { parseWireSockSnapshot } from "../src/network/vpn-snapshot.ts";
import { powerShellCommandArgs, quotePowerShell } from "../src/network/powershell.ts";

// `vpn-windows.ts` cannot be imported directly by Node's test runner (extensionless TS
// imports), so it is bundled in memory once and imported through a data URL. The ownership
// classification is the part under test: which active resources are positively proven to
// belong to this app's exact profile.
const here = path.dirname(fileURLToPath(import.meta.url));
const bundled = await build({
  entryPoints: [path.join(here, "..", "src", "network", "vpn-windows.ts")],
  bundle: true, format: "esm", platform: "node", write: false, logLevel: "silent",
});
const { inspectWireSock, inspectWireSockAsync } = await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString("base64")}`);

const config = String.raw`C:\native-data\native-wiresock.conf`;
const guiConfig = String.raw`C:\Users\someone\AppData\Roaming\ProtonVPN\proton.conf`;
const client = String.raw`C:\Program Files\WireSock\wiresock-client.exe`;
const nativeLine = `"${client}" -config "${config}"`;
const service = (command, processId = 10) => ({ name: "wiresock-client-service", running: true, command, processId });

for (const failedQuery of ["Get-Service", "Win32_Service", "Win32_Process"]) test(`a failed ${failedQuery} query never proves tunnel absence`, { skip: process.platform !== "win32" }, () => {
  const execute = childProcess.execFileSync;
  try {
    const fixture = `function Get-Service { [CmdletBinding()] param($Name) if('${failedQuery}' -eq 'Get-Service'){Write-Error 'FIXTURE_SERVICE_FAILURE'}; [pscustomobject]@{Status='Running'} }; function Get-CimInstance { [CmdletBinding()] param($ClassName,$Filter) if($ClassName -eq '${failedQuery}'){Write-Error 'FIXTURE_CIM_FAILURE'}; if($ClassName -eq 'Win32_Service'){[pscustomobject]@{Name=$Filter.Substring(6,$Filter.Length-7);State='Running';PathName='fixture.exe';ProcessId=10}} }; `;
    mock.method(childProcess, "execFileSync", (file, args, options) => {
      assert.equal(file, "powershell.exe");
      return execute(file, [...args.slice(0, -1), fixture + args.at(-1)], options);
    });
    syncBuiltinESMExports();
    const result = inspectWireSock(config);
    assert.equal(result.reliable, false);
    assert.equal(result.owned, false);
    assert.equal(result.origin, "unknown");
  } finally { mock.restoreAll(); syncBuiltinESMExports(); inspectWireSock(config, undefined, { services: [], processes: [] }); }
});

test("failed inspections back off for 30 seconds and successful reads are always fresh", { skip: process.platform !== "win32" }, () => {
  let time = 100, queries = 0, scReads = 0, failed = true;
  try {
    mock.method(Date, "now", () => time);
    mock.method(childProcess, "execFileSync", (file, args) => {
      if (file === "sc.exe") { scReads++; return "STATE : 1 STOPPED"; }
      assert.equal(file, "powershell.exe");
      queries++;
      if (failed) throw new Error("fixture timeout");
      return JSON.stringify({ services: ["wiresock-client-service", "wiresock-pro-client-service"].map(name => ({ name, state: "Missing" })), processes: [], processesReliable: true });
    });
    syncBuiltinESMExports();
    assert.equal(inspectWireSock(config).reliable, false);
    assert.equal(inspectWireSock(config).reliable, false);
    assert.equal(queries, 1);
    assert.equal(scReads, 4);
    time += 29_999;
    assert.equal(inspectWireSock(config).reliable, false);
    assert.equal(queries, 1);
    time++;
    failed = false;
    assert.equal(inspectWireSock(config).reliable, true);
    assert.equal(inspectWireSock(config).reliable, true);
    assert.equal(queries, 3);
    failed = true;
    assert.equal(inspectWireSock(config).reliable, false);
    assert.equal(queries, 4);
    inspectWireSock(config, undefined, { services: [], processes: [] });
    failed = false;
    assert.equal(inspectWireSock(config).reliable, true);
    assert.equal(queries, 5, "a successful observation clears the failure window");
  } finally { mock.restoreAll(); syncBuiltinESMExports(); inspectWireSock(config, undefined, { services: [], processes: [] }); }
});

test("unknown service or process reads remain unknown while successful empty reads prove absence", () => {
  const names = ["wiresock-client-service"];
  const raw = { services: [{ name: names[0], state: "Missing" }], processes: [], processesReliable: true };
  assert.equal(inspectWireSock(config, undefined, parseWireSockSnapshot(JSON.stringify(raw), names)).reliable, true);
  for (const changed of [
    { ...raw, services: [{ name: names[0], state: "Unknown" }] },
    { ...raw, processesReliable: false },
    { ...raw, processes: null },
  ]) {
    const snapshot = parseWireSockSnapshot(JSON.stringify(changed), names);
    assert.ok(snapshot);
    assert.equal(inspectWireSock(config, undefined, snapshot).reliable, false);
  }
});

for (const threaded of [false, true]) test(`real PowerShell preserves accented snapshot ownership (${threaded ? "worker" : "sync"})`, { skip: process.platform !== "win32" }, async () => {
  // Windows can hand back an 8.3 temp path while PowerShell reports the long form, so compare on the long one.
  const root = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), "brisa-snapshot-encoding-")));
  const workers = [];
  const execute = childProcess.execFileSync;
  const Worker = workerThreads.Worker;
  try {
    const ownedConfig = path.join(root, "Jo\u00e3o", "native-wiresock.conf");
    fs.mkdirSync(path.dirname(ownedConfig));
    fs.writeFileSync(ownedConfig, "synthetic fixture");
    const literal = `'${ownedConfig.replaceAll("'", "''")}'`;
    const fixture = `function Get-Service { param($Name) [pscustomobject]@{Status='Stopped'} }; function Get-ItemProperty { param($LiteralPath,$Name) [pscustomobject]@{ImagePath='fixture.exe'} }; function Get-CimInstance { param($ClassName,$Filter) if($ClassName -eq 'Win32_Process'){ [pscustomobject]@{ProcessId=42;CommandLine=('wiresock-client.exe -config "'+(Get-Item -LiteralPath ${literal}).FullName+'"')} } }; `;
    mock.method(childProcess, "execFileSync", (file, args, options) => execute(file, [...args.slice(0, -1), fixture + args.at(-1)], options));
    mock.method(workerThreads, "Worker", function (...args) {
      const worker = new Worker(...args);
      const postMessage = worker.postMessage.bind(worker);
      worker.postMessage = request => postMessage({ ...request, script: fixture + request.script });
      workers.push(worker);
      return worker;
    });
    syncBuiltinESMExports();
    const result = threaded ? await inspectWireSockAsync(ownedConfig) : inspectWireSock(ownedConfig);
    assert.equal(workers.length, threaded ? 1 : 0);
    assert.equal(result.reliable, true);
    assert.equal(result.owned, true);
    assert.deepEqual(result.ownProcessIds, [42]);
  } finally {
    mock.restoreAll();
    syncBuiltinESMExports();
    await Promise.all(workers.map(worker => worker.terminate()));
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("a native tunnel next to a foreign one reports only the native resources as owned", () => {
  const result = inspectWireSock(config, guiConfig, {
    services: [service(nativeLine)],
    processes: [
      { pid: 10, commandLine: nativeLine },
      { pid: 99, commandLine: `"${client}" -config "C:\\other\\route.conf"` },
    ],
  });
  assert.equal(result.reliable, true);
  assert.equal(result.owned, false);
  assert.equal(result.origin, "mixed");
  assert.deepEqual(result.ownServices, ["wiresock-client-service"]);
  assert.deepEqual(result.ownProcessIds, [10]);
});

test("a native tunnel next to the managed GUI keeps the GUI resources out of the owned set", () => {
  const guiLine = `"${client}" -config "${guiConfig}"`;
  const result = inspectWireSock(config, guiConfig, {
    services: [service(nativeLine)],
    processes: [
      { pid: 10, commandLine: nativeLine },
      { pid: 20, commandLine: guiLine },
    ],
  });
  assert.equal(result.reliable, true);
  assert.equal(result.owned, false);
  assert.deepEqual(result.ownServices, ["wiresock-client-service"]);
  assert.deepEqual(result.ownProcessIds, [10]);
});

test("a foreign tunnel alone proves no ownership", () => {
  const result = inspectWireSock(config, guiConfig, {
    services: [],
    processes: [{ pid: 99, commandLine: `"${client}" -config "C:\\other\\route.conf"` }],
  });
  assert.equal(result.reliable, true);
  assert.equal(result.owned, false);
  assert.deepEqual(result.ownServices, []);
  assert.deepEqual(result.ownProcessIds, []);
});

test("an unrelated unreadable process does not erase a proven native process", () => {
  const result = inspectWireSock(config, guiConfig, {
    services: [{ name: "wiresock-client-service", running: false, command: null, processId: null }],
    processes: [
      { pid: 10, commandLine: nativeLine },
      { pid: 77, commandLine: null },
    ],
  });
  assert.equal(result.reliable, false);
  assert.equal(result.owned, false);
  assert.deepEqual(result.ownProcessIds, [10]);
});

test("an unreadable service does not erase a proven native process", () => {
  const result = inspectWireSock(config, guiConfig, {
    services: [
      { name: "wiresock-pro-client-service", running: null, command: null, processId: null },
      service(nativeLine, 10),
    ],
    processes: [{ pid: 10, commandLine: nativeLine }],
  });
  assert.equal(result.reliable, false);
  assert.deepEqual(result.ownProcessIds, [10]);
  assert.deepEqual(result.ownServices, ["wiresock-client-service"]);
});

test("the config flag matches exactly, quoted or not, and never by substring", () => {
  const unquoted = inspectWireSock(config, guiConfig, {
    services: [], processes: [{ pid: 10, commandLine: `${client} -config ${config}` }],
  });
  assert.deepEqual(unquoted.ownProcessIds, [10]);

  const backup = inspectWireSock(config, guiConfig, {
    services: [], processes: [{ pid: 10, commandLine: `${client} -config "${config}.bak"` }],
  });
  assert.deepEqual(backup.ownProcessIds, []);
  assert.equal(backup.owned, false);

  const prefix = inspectWireSock(config, guiConfig, {
    services: [], processes: [{ pid: 10, commandLine: `${client} -allowed-apps "${config}"` }],
  });
  assert.deepEqual(prefix.ownProcessIds, []);
});


// Compile only the real script generator; no live inspect or mutation is invoked here.
const vpnSource = fs.readFileSync(path.join(here, "../src/network/vpn-windows.ts"), "utf8");
const generatorText = vpnSource.slice(vpnSource.indexOf("function wireSockSnapshotScript("), vpnSource.indexOf("function readWireSockSnapshot("));
const snapshotScript = new Function("quotePowerShell", `${transformSync(generatorText, { loader: "ts", format: "esm" }).code}\nreturn wireSockSnapshotScript;`)(quotePowerShell);
const fixturePrelude = `$script:queries=@();
function Get-CimInstance { [CmdletBinding()] param($ClassName,$Filter) $script:queries += $ClassName; if($ClassName -eq 'Win32_Service'){throw 'UNEXPECTED_SERVICE_CIM'} }
`;
function snapshotFixture(prefix, names = ["wiresock-nonexistent-test"]) {
  const script = fixturePrelude + prefix + `\n$wire = & { ${snapshotScript(names)} }; [pscustomobject]@{snapshot=($wire|ConvertFrom-Json);queries=@($script:queries)} | ConvertTo-Json -Compress -Depth 8`;
  return JSON.parse(childProcess.execFileSync("powershell.exe", powerShellCommandArgs(script), { encoding: "utf8", windowsHide: true, timeout: 10000 }));
}

test("speed S1 real missing service is Missing without service CIM", { skip: process.platform !== "win32" }, () => {
  const result = snapshotFixture("");
  assert.equal(result.snapshot.services[0].state, "Missing");
  assert.deepEqual(result.queries, ["Win32_Process"]);
  assert.equal(result.snapshot.processesReliable, true);
});

for (const failure of ["Write-Error 'FIXTURE_SERVICE_FAILURE'", "throw (New-Object ComponentModel.Win32Exception(5))"]) {
  test(`speed S1 Get-Service failure stays Unknown: ${failure}`, { skip: process.platform !== "win32" }, () => {
    const result = snapshotFixture(`function Get-Service { [CmdletBinding()] param($Name) ${failure} }`);
    assert.equal(result.snapshot.services[0].state, "Unknown");
    assert.deepEqual(result.queries, ["Win32_Process"]);
  });
}

test("speed S1 stopped service keeps Unicode ImagePath without service CIM", { skip: process.platform !== "win32" }, () => {
  const command = String.raw`"C:\João's 東京\wiresock-client.exe" -config "C:\fixture\owned.conf"`;
  const result = snapshotFixture(String.raw`function Get-Service { [CmdletBinding()] param($Name) [pscustomobject]@{Status='Stopped'} }; function Get-ItemProperty { [CmdletBinding()] param($LiteralPath,$Name) if($Name -ne 'ImagePath' -or $LiteralPath -ne 'HKLM:\SYSTEM\CurrentControlSet\Services\wiresock-nonexistent-test'){throw 'BAD_REGISTRY_QUERY'}; [pscustomobject]@{ImagePath=${quotePowerShell(command)}} }`);
  assert.equal(result.snapshot.services[0].state, "Stopped");
  assert.equal(result.snapshot.services[0].command, command);
  assert.equal(result.snapshot.services[0].processId, 0);
  assert.deepEqual(result.queries, ["Win32_Process"]);
});

for (const property of ["throw 'FIXTURE_REGISTRY_DENIED'", "[pscustomobject]@{ImagePath=$null}"]) {
  test(`speed S1 unreadable stopped ImagePath is Unknown: ${property}`, { skip: process.platform !== "win32" }, () => {
    const result = snapshotFixture(`function Get-Service { [CmdletBinding()] param($Name) [pscustomobject]@{Status='Stopped'} }; function Get-ItemProperty { [CmdletBinding()] param($LiteralPath,$Name) ${property} }`);
    assert.equal(result.snapshot.services[0].state, "Unknown");
    assert.deepEqual(result.queries, ["Win32_Process"]);
  });
}

for (const status of ["Running", "StartPending", "StopPending", "Paused", "PausePending", "ContinuePending"]) {
  test(`speed S1 ${status} uses scoped authoritative CIM`, { skip: process.platform !== "win32" }, () => {
    const result = snapshotFixture(`function Get-Service { [CmdletBinding()] param($Name) $script:queries+='Get-Service'; [pscustomobject]@{Status='${status}'} }; function Get-CimInstance { [CmdletBinding()] param($ClassName,$Filter) $script:queries += $ClassName; if($ClassName -eq 'Win32_Service'){if($Filter -ne "Name='wiresock-nonexistent-test'"){throw 'UNSCOPED_CIM'}; [pscustomobject]@{Name='wiresock-nonexistent-test';State='${status}';PathName='fixture.exe';ProcessId=42}} }`);
    assert.equal(result.snapshot.services[0].state, status);
    assert.equal(result.snapshot.services[0].processId, 42);
    assert.deepEqual(result.queries, ["Get-Service", "Win32_Service", "Win32_Process"]);
  });
}

for (const fallback of ["throw 'FIXTURE_CIM_DENIED'", "$null"]) {
  test(`speed S1 non-stopped missing CIM evidence is Unknown: ${fallback}`, { skip: process.platform !== "win32" }, () => {
    const result = snapshotFixture(`function Get-Service { [CmdletBinding()] param($Name) [pscustomobject]@{Status='Running'} }; function Get-CimInstance { [CmdletBinding()] param($ClassName,$Filter) $script:queries += $ClassName; if($ClassName -eq 'Win32_Service'){${fallback}} }`);
    assert.equal(result.snapshot.services[0].state, "Unknown");
  });
}

test("speed S1 keeps the parser contract and adds no sc text fallback", () => {
  const script = snapshotScript(["fixture"]);
  assert.ok(script.includes("[PSCustomObject]@{ services=$svc; processes=$procs; processesReliable=$processesReliable } | ConvertTo-Json -Compress -Depth 4"));
  assert.doesNotMatch(script, /sc[.]exe|STATE\s*:/);
});
