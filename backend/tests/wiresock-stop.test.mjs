import { build } from "esbuild";
import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// The live stop path is async and takes an injectable runtime, so the whole ordering can be
// proved without launching sc.exe, taskkill or WireSock. `vpn-windows.ts` needs bundling for
// the same reason as the ownership suite: extensionless TS imports.
const here = path.dirname(fileURLToPath(import.meta.url));
const bundled = await build({
  entryPoints: [path.join(here, "..", "src", "network", "vpn-windows.ts")],
  bundle: true, format: "esm", platform: "node", write: false, logLevel: "silent",
});
const { stopOwnedWireSock, defaultStopRuntime } = await import(`data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString("base64")}`);

const config = String.raw`C:\native-data\native-wiresock.conf`;
const client = String.raw`C:\Program Files\WireSock\wiresock-client.exe`;
const ownLine = `"${client}" -config "${config}"`;
const serviceName = "wiresock-client-service";

const activeOwned = {
  services: [{ name: serviceName, running: true, command: ownLine, processId: 42 }],
  processes: [{ pid: 42, commandLine: ownLine }],
};
const activeForeign = {
  services: [{ name: serviceName, running: true, command: `"${client}" -config "C:\\other\\route.conf"`, processId: 77 }],
  processes: [{ pid: 77, commandLine: `"${client}" -config "C:\\other\\route.conf"` }],
};
const inactive = { services: [{ name: serviceName, running: false, command: null, processId: 0 }], processes: [] };

const quietLog = () => {};

// A real pair on disk, so the lease freshness check exercises stat plus SHA-256 rather than a
// fixture that only looks like a file.
function sessionLease() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "brisa-lease-"));
  const executable = path.join(directory, "wiresock-client.exe");
  const booster = path.join(directory, "wgbooster.dll");
  fs.writeFileSync(executable, "synthetic-executable-bytes");
  fs.writeFileSync(booster, "synthetic-booster-bytes");
  const digest = file => crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
  return {
    lease: {
      executable,
      booster,
      executableVersion: "3.4.8.1",
      boosterVersion: "3.4.8.1",
      executableDigest: digest(executable),
      boosterDigest: digest(booster),
    },
  };
}

function fakeRuntime({ snapshots, results = {}, advanceMs = 0, deferAtSnapshot = 0 }) {
  const calls = [];
  const clock = { t: 0 };
  let index = 0;
  let snapshotCount = 0;
  const deferred = {};
  deferred.promise = new Promise(resolve => { deferred.resolve = resolve; });
  return {
    calls,
    clock,
    deferred,
    runtime: {
      now: () => clock.t,
      sleep: async (ms) => { clock.t += ms; },
      snapshot: async () => {
        calls.push("snapshot");
        snapshotCount += 1;
        clock.t += advanceMs;
        const snapshot = snapshots[Math.min(index, snapshots.length - 1)];
        index += 1;
        if (deferAtSnapshot > 0 && snapshotCount === deferAtSnapshot) return deferred.promise.then(() => snapshot);
        return snapshot;
      },
      run: async (file, args) => {
        calls.push(`${path.win32.basename(file)} ${args.join(" ")}`);
        clock.t += advanceMs;
        return results[path.win32.basename(file)] ?? { ok: true, exitCode: 0, timedOut: false, aborted: false, stdout: "" };
      },
    },
  };
}

test("stop stays unsettled until a fresh reliable inactive observation, in strict order", async () => {
  const { runtime, calls } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive] });
  const result = await stopOwnedWireSock(config, quietLog, { runtime });
  assert.equal(result.stopped, true);
  // Every observation follows the action it must see the effect of, and nothing runs in parallel.
  assert.deepEqual(calls.map(call => call.split(" ")[0] + (call.startsWith("sc.exe") ? " stop" : "")), [
    "snapshot",
    "sc.exe stop",
    "snapshot",
    "snapshot",
    "powershell.exe",
    "snapshot",
    "ipconfig.exe",
    "snapshot",
  ]);
});

test("the event loop keeps running while a child process is pending", async () => {
  const { runtime } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive] });
  const pending = stopOwnedWireSock(config, quietLog, { runtime });
  let timerFired = false;
  const timer = setTimeout(() => { timerFired = true; }, 0);
  await new Promise(resolve => setTimeout(resolve, 5));
  clearTimeout(timer);
  assert.equal(timerFired, true, "a pending stop must not block timers");
  await pending;
});

test("a foreign or unreliable initial state never issues a destructive command", async () => {
  for (const [label, snapshot] of [["foreign", activeForeign], ["unreliable", null]]) {
    const { runtime, calls } = fakeRuntime({ snapshots: [snapshot] });
    const result = await stopOwnedWireSock(config, quietLog, { runtime });
    assert.equal(result.stopped, false, label);
    assert.deepEqual(calls, ["snapshot"], `${label} must not run sc/taskkill/cleanup`);
  }
});

test("a stop request that does not end the tunnel reports not stopped", async () => {
  const { runtime } = fakeRuntime({ snapshots: [activeOwned] });
  const result = await stopOwnedWireSock(config, quietLog, { runtime });
  assert.equal(result.stopped, false);
  assert.match(result.error, /permanece ativo|externo|desconhecido/i);
});

test("a pre-aborted signal performs no work at all", async () => {
  const { runtime, calls } = fakeRuntime({ snapshots: [activeOwned] });
  const controller = new AbortController();
  controller.abort();
  const result = await stopOwnedWireSock(config, quietLog, { runtime, signal: controller.signal });
  assert.equal(result.stopped, false);
  assert.equal(result.error, "STOP_CANCELLED");
  assert.deepEqual(calls, []);
});

test("an expired total budget never turns into a success", async () => {
  const { runtime } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive], advanceMs: 20 });
  const result = await stopOwnedWireSock(config, quietLog, { runtime, timeoutMs: 50 });
  assert.equal(result.stopped, false);
  assert.equal(result.error, "STOP_TIMEOUT");
});

test("optional network cleanup is skipped without a session executable", async () => {
  const { runtime, calls } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive] });
  const result = await stopOwnedWireSock(config, quietLog, { runtime });
  assert.equal(result.networkLockReset, false);
  assert.ok(!calls.some(call => call.startsWith("wiresock-client.exe")), "stop must never discover or run the SDK");
});

test("a fresh session lease is used for the optional lock reset exactly once", async () => {
  const { runtime, calls } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive] });
  const { lease } = sessionLease();
  const result = await stopOwnedWireSock(config, quietLog, { runtime, candidateLease: lease, isTrusted: () => true });
  assert.equal(result.networkLockReset, true);
  assert.equal(calls.filter(call => call === "wiresock-client.exe reset-network-lock").length, 1);
});

test("a stale lease skips the optional reset instead of running changed bytes", async () => {
  const { runtime, calls } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive] });
  const { lease } = sessionLease();
  // Same path and same mtime, different bytes: only the digest can catch this.
  fs.writeFileSync(lease.executable, "tampered-bytes");
  const result = await stopOwnedWireSock(config, quietLog, { runtime, candidateLease: lease, isTrusted: () => true });
  assert.equal(result.stopped, true, "the tunnel itself is still reported stopped");
  assert.equal(result.networkLockReset, false);
  assert.ok(!calls.some(call => call.startsWith("wiresock-client.exe")), "no reset may run on stale evidence");
});

test("a budget that expires during the freshness proof never runs the reset", async () => {
  // The probe stands in for stalled stat/hash I/O: enough budget is left to reach it, and it
  // burns the rest, so the post-await check must time out and the SDK must never run.
  const { runtime, calls, clock } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive, inactive] });
  const { lease } = sessionLease();
  let probes = 0;
  const result = await stopOwnedWireSock(config, quietLog, {
    runtime,
    candidateLease: lease, isTrusted: () => true,
    timeoutMs: 5_000,
    freshness: async () => { probes += 1; clock.t += 10_000; return true; },
  });
  assert.equal(probes, 1, "the freshness proof must actually run");
  assert.equal(result.stopped, false);
  assert.equal(result.networkLockReset, false);
  assert.ok(!calls.some(call => call.startsWith("wiresock-client.exe")), "a reset must not run without budget");
});

test("a freshness proof that never settles still times out inside the budget", async () => {
  // Exercises the real withinDeadline timer, not only the post-await check: a hash stuck on I/O
  // cannot outlive the cleanup budget.
  const { runtime, calls } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive, inactive] });
  const { lease } = sessionLease();
  const started = Date.now();
  const result = await stopOwnedWireSock(config, quietLog, {
    runtime,
    candidateLease: lease, isTrusted: () => true,
    timeoutMs: 700,
    freshness: () => new Promise(() => {}),
  });
  assert.equal(result.stopped, false);
  assert.ok(Date.now() - started < 5_000, "the stop must not hang on a stuck freshness proof");
  assert.ok(!calls.some(call => call.startsWith("wiresock-client.exe")), "a reset must not run without budget");
});

test("a foreign tunnel appearing after the freshness proof skips the reset", async () => {
  // The proof succeeds without touching the clock, and the gate immediately after it sees a
  // foreign tunnel, so the reset must not run against a network Brisa does not own.
  const { runtime, calls } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive, activeForeign] });
  const { lease } = sessionLease();
  const result = await stopOwnedWireSock(config, quietLog, {
    runtime,
    candidateLease: lease, isTrusted: () => true,
    freshness: async () => true,
  });
  assert.equal(result.stopped, false);
  assert.ok(!calls.some(call => call.startsWith("wiresock-client.exe")), "a foreign tunnel must not receive a reset");
});

test("an already stopped tunnel succeeds without touching anything", async () => {
  const { runtime, calls } = fakeRuntime({ snapshots: [inactive] });
  const result = await stopOwnedWireSock(config, quietLog, { runtime });
  assert.equal(result.stopped, true);
  assert.deepEqual(calls, ["snapshot"]);
});

test("an abort during the final inspection never returns stopped", async () => {
  // Five inspections: initial, loop, before DNS, before flush, final verdict.
  const { runtime, deferred } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive, inactive, inactive], deferAtSnapshot: 5 });
  const controller = new AbortController();
  const pending = stopOwnedWireSock(config, quietLog, { runtime, signal: controller.signal });
  await new Promise(resolve => setTimeout(resolve, 5));
  controller.abort();
  deferred.resolve();
  const result = await pending;
  assert.equal(result.stopped, false);
  assert.equal(result.error, "STOP_CANCELLED");
});

test("a foreign tunnel appearing mid-cleanup stops the remaining global actions", async () => {
  // The DNS reset runs, then the flushdns gate sees a foreign tunnel and must refuse it.
  const { runtime, calls } = fakeRuntime({ snapshots: [activeOwned, inactive, inactive, activeForeign] });
  const result = await stopOwnedWireSock(config, quietLog, { runtime });
  assert.equal(result.stopped, false);
  assert.ok(!calls.some(call => call.startsWith("ipconfig.exe")), "flushdns must not touch a foreign tunnel's network");
});

test("an abort during the first inspection is reported as cancelled, not as unknown state", async () => {
  const { runtime, deferred } = fakeRuntime({ snapshots: [activeOwned], deferAtSnapshot: 1 });
  const controller = new AbortController();
  const pending = stopOwnedWireSock(config, quietLog, { runtime, signal: controller.signal });
  await new Promise(resolve => setTimeout(resolve, 5));
  controller.abort();
  deferred.resolve();
  const result = await pending;
  assert.equal(result.stopped, false);
  assert.equal(result.error, "STOP_CANCELLED");
});

test("a budget expiring during the first inspection is reported as a timeout", async () => {
  const { runtime } = fakeRuntime({ snapshots: [activeOwned], advanceMs: 60 });
  const result = await stopOwnedWireSock(config, quietLog, { runtime, timeoutMs: 50 });
  assert.equal(result.stopped, false);
  assert.equal(result.error, "STOP_TIMEOUT");
});

test("an aborted command reports aborted and settles exactly once", async () => {
  // The close-before-resolve ordering itself is structural, not timed: on this platform the
  // gap between execFile's abort callback and 'close' measured ~4 ms, so a timing assertion
  // here would be flaky. What this covers is that an abort still produces one settled result.
  const controller = new AbortController();
  const pending = defaultStopRuntime.run(process.execPath, ["-e", "setTimeout(()=>{}, 400)"], 5000, controller.signal);
  controller.abort();
  const result = await pending;
  assert.equal(result.aborted, true);
  assert.equal(result.ok, false);
});
