import { build } from "esbuild";
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

for (const failedQuery of ["Win32_Service", "Win32_Process"]) test(`a failed ${failedQuery} query never proves tunnel absence`, { skip: process.platform !== "win32" }, () => {
  const execute = childProcess.execFileSync;
  try {
    const fixture = `function Get-CimInstance { [CmdletBinding()] param($ClassName,$Filter) if($ClassName -eq '${failedQuery}'){Write-Error 'FIXTURE_CIM_FAILURE'} }; `;
    mock.method(childProcess, "execFileSync", (file, args, options) => {
      assert.equal(file, "powershell.exe");
      return execute(file, [...args.slice(0, -1), fixture + args.at(-1)], options);
    });
    syncBuiltinESMExports();
    const result = inspectWireSock(config);
    assert.equal(result.reliable, false);
    assert.equal(result.owned, false);
    assert.equal(result.origin, "unknown");
  } finally { mock.restoreAll(); syncBuiltinESMExports(); }
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
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "brisa-snapshot-encoding-"));
  const workers = [];
  const execute = childProcess.execFileSync;
  const Worker = workerThreads.Worker;
  try {
    const ownedConfig = path.join(root, "Jo\u00e3o", "native-wiresock.conf");
    fs.mkdirSync(path.dirname(ownedConfig));
    fs.writeFileSync(ownedConfig, "synthetic fixture");
    const literal = `'${ownedConfig.replaceAll("'", "''")}'`;
    const fixture = `function Get-CimInstance { param($ClassName,$Filter) if($ClassName -eq 'Win32_Process'){ [pscustomobject]@{ProcessId=42;CommandLine=('wiresock-client.exe -config "'+(Get-Item -LiteralPath ${literal}).FullName+'"')} } }; `;
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
