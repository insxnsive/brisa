import { build } from "esbuild";
import assert from "node:assert/strict";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// `vpn-windows.ts` cannot be imported directly by Node's test runner (extensionless TS
// imports), so it is bundled in memory once and imported through a data URL. The ownership
// classification is the part under test: which active resources are positively proven to
// belong to this app's exact profile.
const here = path.dirname(fileURLToPath(import.meta.url));
const bundled = await build({
  entryPoints: [path.join(here, "..", "src", "network", "vpn-windows.ts")],
  bundle: true, format: "esm", platform: "node", write: false, logLevel: "silent",
});
const { inspectWireSock } = await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString("base64")}`);

const config = String.raw`C:\native-data\native-wiresock.conf`;
const guiConfig = String.raw`C:\Users\someone\AppData\Roaming\ProtonVPN\proton.conf`;
const client = String.raw`C:\Program Files\WireSock\wiresock-client.exe`;
const nativeLine = `"${client}" -config "${config}"`;
const service = (command, processId = 10) => ({ name: "wiresock-client-service", running: true, command, processId });

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
