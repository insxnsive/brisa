import assert from "node:assert/strict";
import test from "node:test";

import { WireSockSetupError, wireSockSetupFailure } from "../src/wiresock-setup.mjs";
import { sanitizeWireGuardConfig, validateWireGuardConfig } from "../src/network/vpn-types.ts";
import { createDiagnostics } from "../src/diagnostics.mjs";
import { WireSockStartError } from "../src/wiresock-start-result.mjs";

import { createBackend } from "../src/backend.mjs";
import { DiscordCompatibilityError, createDiscordSelectionGuard } from "../src/discord-compatibility.mjs";

const goodConfig = `[Interface]\nPrivateKey = ${"A".repeat(43)}=\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = ${"B".repeat(43)}=\nAllowedIPs = 0.0.0.0/0\nEndpoint = vpn.example:51820\n`;

function harness(overrides = {}) {
  const calls = [];
  const files = new Map([["C:\\picked\\route.conf", goodConfig]]);
  let inspection = { active: false, owned: false, reliable: true, reason: null };
  const deps = {
    inspect: async () => inspection,
    getSavedSessionUsername: async () => "own-user",
    getPlan: async () => ({ success: false, status: "unknown" }),
    login: async (_dir, username, _password, _twoFactor, _token, operation, method) => {
      calls.push(["login", username, method, operation.signal]);
      return { success: true, username };
    },
    generate: async (_dir, options) => {
      calls.push(["generate", options]);
      return { success: true, server: "NL#1", country: "NL", pingMs: 34, confFile: "generated.conf" };
    },
    start: async (configPath, raw, apps) => { calls.push(["start", configPath, raw, apps]); return { configPath }; },
    stop: async configPath => { calls.push(["stop", configPath]); inspection = { active: false, owned: false, reliable: true }; return { stopped: true }; },
    validateConfig: raw => raw === goodConfig ? { valid: true } : { valid: false, error: "invalid profile" },
    sanitizeConfig: (raw, allowed) => `${raw}#@ws:AllowedApps = ${allowed}`,
    discoverDiscord: async () => ["C:\\Discord\\Discord.exe"],
    stopDiscord: async apps => { calls.push(["close-discord", apps]); },
    launchDiscord: async apps => { calls.push(["launch", apps]); },
    discordRunning: async () => true,
    verifyRoute: async () => ({ verified: true, reason: "verified" }),
    helperAvailable: () => true,
    files: {
      readExplicit: async p => {
        if (!files.has(p)) throw new Error("missing");
        return files.get(p);
      },
      readOwned: async p => files.get(p) ?? "",
      writeOwned: async (p, value) => { files.set(p, value); calls.push(["write", p]); },
      removeOwned: async p => { files.delete(p); calls.push(["remove", p]); },
      existsOwned: async p => files.has(p),
    },
    ...overrides,
  };
  const backend = createBackend(deps, {
    dataDir: "C:\\native-data",
    statePath: "C:\\native-data\\state.json",
    sessionPath: "C:\\native-data\\proton-session.json",
    ownedConfigPath: "C:\\native-data\\native-wiresock.conf",
    importedProfilePath: "C:\\native-data\\imported.conf",
    generatedProfilePath: "C:\\native-data\\wireguard.conf",
  });
  return { backend, calls, files, setInspection(value) { inspection = value; } };
}

test("missing Discord gives update guidance before profile generation or any process change", async () => {
  const { backend, calls } = harness({ discoverDiscord: async () => [] });
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.equal(result.code, "DISCORD_UNAVAILABLE");
  assert.match(result.message, /install or update Discord/i);
  assert.match(result.message, /open it once/i);
  assert.deepEqual(calls, []);
});

test("Discord changing after profile read is reported before stopping any process", async () => {
  const { backend, calls, files } = harness({
    validateDiscordApps: async () => { throw new DiscordCompatibilityError("DISCORD_CHANGED"); },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.equal(result.code, "DISCORD_CHANGED");
  assert.match(result.message, /Finish updating Discord/i);
  assert.deepEqual(calls, []);
});

test("a compatibility failure at the process boundary retains actionable update guidance", async () => {
  const { backend, calls, files } = harness({
    stopDiscord: async () => { throw new DiscordCompatibilityError("DISCORD_CHANGED"); },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.equal(result.code, "DISCORD_CHANGED");
  assert.match(result.message, /Finish updating Discord/i);
  assert.equal(calls.some(c => c[0] === "start"), false);
});

test("Discord discovery errors do not leak paths and do not start anything", async () => {
  const { backend, calls } = harness({ discoverDiscord: async () => { throw new Error("C:\\private\\synthetic-user\\token"); } });
  const result = await backend.execute("connect", {});
  assert.equal(result.code, "DISCORD_CHECK_FAILED");
  assert.match(result.message, /restart Discord/i);
  assert.doesNotMatch(JSON.stringify(result), /private|synthetic-user|token/);
  assert.deepEqual(calls, []);
});

test("missing Discord during import returns guidance without saving a profile", async () => {
  const { backend, calls } = harness({ discoverDiscord: async () => [] });
  const result = await backend.execute("importConfig", { path: "C:\\picked\\route.conf" });
  assert.equal(result.code, "DISCORD_UNAVAILABLE");
  assert.deepEqual(calls, []);
});

test("a version change during optimization is rediscovered rather than pinned", async () => {
  let current = "C:\\Discord\\app-1.0.9259\\Discord.exe";
  const old = "C:\\Discord\\app-1.0.9258\\Discord.exe";
  const { backend, calls, files, setInspection } = harness({
    discoverDiscord: async () => [current],
    // Production start returns only after the service is up, so the proof sees it active.
    start: async (...args) => { calls.push(["start", ...args]); setInspection({ active: true, owned: true, reliable: true }); },
    generate: async () => {
      current = old;
      files.set("C:\\native-data\\wireguard.conf", goodConfig);
      return { success: true };
    },
  });
  assert.equal((await backend.execute("connect", {})).success, true);
  assert.deepEqual(calls.find(c => c[0] === "start")[3], [old]);
  assert.deepEqual(calls.find(c => c[0] === "launch")[1], [old]);
});

test("update-race guidance never claims restoration when cleanup fails", async () => {
  const { backend, calls, files } = harness({
    stopDiscord: async () => { throw new DiscordCompatibilityError("DISCORD_CHANGED"); },
    launchDiscord: async () => { throw new DiscordCompatibilityError("DISCORD_CHANGED"); },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.code, "DISCORD_CHANGED");
  assert.match(result.message, /could not confirm tunnel cleanup/i);
  assert.equal(calls.some(c => c[0] === "start"), false);
});

for (const timing of ["startup", "connected"]) {
  test(`Discord updating while ${timing} cannot block owned-tunnel removal`, async () => {
    const old = "C:\\Discord\\app-1.0.9258\\Discord.exe";
    const updated = "C:\\Discord\\app-1.0.9259\\Discord.exe";
    let current = [old];
    const guard = createDiscordSelectionGuard(() => current);
    const { backend, calls, files, setInspection } = harness({
      discoverDiscord: async () => current,
      validateDiscordApps: guard.assertCurrent,
      stopDiscord: async (apps, _signal, { cleanup = false } = {}) => {
        (cleanup ? guard.assertCleanup : guard.assertCurrent)(apps);
        calls.push(["close-discord", apps]);
      },
      launchDiscord: async apps => guard.assertCurrent(apps),
      start: async () => {
        setInspection({ active: true, owned: true, reliable: true });
        if (timing === "startup") current = [updated];
      },
    });
    files.set("C:\\native-data\\wireguard.conf", goodConfig);
    const result = await backend.execute("connect", {});
    if (timing === "connected") {
      assert.equal(result.success, true);
      current = [updated];
      await backend.execute("disconnect", {});
    } else {
      assert.equal(result.code, "DISCORD_CHANGED");
    }
    assert.equal(calls.filter(call => call[0] === "stop").length, 1, "the verified owned tunnel must be removed");
    assert.equal((await backend.execute("snapshot", {})).tunnelActive, false);
  });
}

test("connect closes old Discord before changing the route and confirms its relaunch", async () => {
  const { backend, calls, files, setInspection } = harness({
    start: async (...args) => { calls.push(["start", ...args]); setInspection({ active: true, owned: true, reliable: true }); },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  assert.equal((await backend.execute("connect", {})).success, true);
  assert.deepEqual(calls.filter(c => ["close-discord", "start", "launch"].includes(c[0])).map(c => c[0]),
    ["close-discord", "start", "launch"]);
});

test("Discord launch failure rolls back only the owned tunnel and is not swallowed", async () => {
  const { backend, calls, files, setInspection } = harness({
    start: async () => { calls.push(["start"]); setInspection({ active: true, owned: true, reliable: true }); },
    launchDiscord: async () => { throw new Error("synthetic spawn failure"); },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.ok(calls.some(c => c[0] === "stop"));
  assert.equal((await backend.execute("snapshot", {})).connected, false);
});

test("failed tunneled relaunch restores Discord only after verified tunnel removal", async () => {
  let launches = 0;
  const { backend, files, setInspection, calls } = harness({
    start: async () => setInspection({ active: true, owned: true, reliable: true }),
    launchDiscord: async () => {
      if (++launches === 1) throw new Error("synthetic first launch failure");
      assert.equal((await backend.execute("snapshot", {})).tunnelActive, false);
      calls.push(["restored"]);
    },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  assert.equal((await backend.execute("connect", {})).success, false);
  assert.equal(launches, 2);
  assert.deepEqual(calls.filter(c => ["stop", "restored"].includes(c[0])).map(c => c[0]), ["stop", "restored"]);
});

test("unverified route returns an explicit warning while keeping Disconnect available", async () => {
  const { backend, files, setInspection } = harness({
    start: async () => setInspection({ active: true, owned: true, reliable: true }),
    verifyRoute: async () => ({ verified: false, reason: "same_as_direct" }),
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.equal(result.code, "ROUTE_UNVERIFIED");
  const snapshot = await backend.execute("snapshot", {});
  assert.equal(snapshot.connected, false);
  assert.equal(snapshot.tunnelActive, true);
});

test("status rechecks route health without keeping a stale verified badge", async () => {
  let now = 1_000, healthy = true, probes = 0;
  const pending = [];
  const { backend, files, setInspection } = harness({
    now: () => now,
    start: async () => setInspection({ active: true, owned: true, reliable: true }),
    verifyRoute: async () => {
      probes++;
      if (probes === 1) return { verified: healthy, reason: healthy ? "verified" : "probe_failed" };
      return new Promise(resolve => pending.push(() => resolve({ verified: healthy, reason: healthy ? "verified" : "probe_failed" })));
    },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  await backend.execute("connect", {});
  assert.equal((await backend.execute("snapshot", {})).connected, true);
  healthy = false; now += 31_000;
  // Item 1A: the re-check runs in the background, so the first stale snapshot keeps the
  // previous verdict and the next one reports the new one.
  const stale = await backend.execute("snapshot", {});
  assert.equal(stale.connected, true);
  assert.equal(stale.routeRechecking, true);
  pending.forEach(release => release());
  await flush();
  assert.equal((await backend.execute("snapshot", {})).connected, false);
  assert.equal(probes, 2);
  assert.equal((await backend.execute("snapshot", {})).tunnelActive, true);
});

const flush = () => new Promise(resolve => setImmediate(resolve));
const gate = () => { let release; const promise = new Promise(resolve => { release = resolve; }); return { promise, release }; };

function routeHarness(overrides = {}) {
  const state = { now: 1_000, calls: 0, signals: [], gates: [] };
  const { backend, files, setInspection } = harness({
    now: () => state.now,
    start: async () => setInspection({ active: true, owned: true, reliable: true }),
    verifyRoute: async signal => {
      state.calls++; state.signals.push(signal);
      if (state.calls === 1) return { verified: true, reason: "verified" };
      const pending = gate();
      state.gates.push(pending);
      return pending.promise;
    },
    ...overrides,
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  return { backend, files, setInspection, state };
}

test("snapshot never blocks on a slow route re-check", async () => {
  const { backend, state } = routeHarness();
  assert.equal((await backend.execute("connect", {})).success, true);
  state.now += 31_000;
  const result = await Promise.race([backend.execute("snapshot", {}),
    new Promise(resolve => setTimeout(() => resolve("blocked"), 500))]);
  assert.notEqual(result, "blocked");
  assert.equal(result.connected, true);
  assert.equal(result.routeRechecking, true);
  assert.ok(result.routeCheckedAgeMs >= 31_000);
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
});

test("concurrent snapshots share a single route re-check", async () => {
  const { backend, state } = routeHarness();
  await backend.execute("connect", {});
  state.now += 31_000;
  await Promise.all([backend.execute("snapshot", {}), backend.execute("snapshot", {})]);
  assert.equal(state.calls, 2, "one connect probe plus one shared re-check");
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
});

test("a fresh re-check completion replaces the stored verdict", async () => {
  const { backend, state } = routeHarness();
  await backend.execute("connect", {});
  state.now += 31_000;
  const stale = await backend.execute("snapshot", {});
  assert.equal(stale.routeRechecking, true);
  state.gates.forEach(g => g.release({ verified: false, reason: "probe_failed" }));
  await flush();
  const fresh = await backend.execute("snapshot", {});
  assert.equal(fresh.connected, false);
  assert.equal(fresh.routeRechecking, false);
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: probe_failed\./);
});

test("a re-check that finishes after disconnect never marks the tunnel verified", async () => {
  const { backend, state } = routeHarness();
  await backend.execute("connect", {});
  state.now += 31_000;
  assert.equal((await backend.execute("snapshot", {})).routeRechecking, true);
  await backend.execute("disconnect", {});
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: not_checked\./);
  assert.equal((await backend.execute("snapshot", {})).connected, false);
});

test("a tunnel that disappears without a Brisa command aborts its re-check", async () => {
  const { backend, state, setInspection } = routeHarness();
  await backend.execute("connect", {});
  state.now += 31_000;
  await backend.execute("snapshot", {});
  const pendingSignal = state.signals.at(-1);
  setInspection({ active: false, owned: false, reliable: true });
  await backend.execute("snapshot", {});
  assert.equal(pendingSignal.aborted, true, "the orphaned probes are aborted, not left to time out");
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
  setInspection({ active: true, owned: true, reliable: true });
  assert.equal((await backend.execute("snapshot", {})).connected, false);
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: not_checked\./);
});

test("a replaced owned tunnel in the same generation drops the old verdict", async () => {
  const { backend, state, setInspection } = routeHarness({
    start: async () => setInspection({ active: true, owned: true, reliable: true, processIds: [10], services: [] }),
  });
  await backend.execute("connect", {});
  state.now += 31_000;
  await backend.execute("snapshot", {});
  setInspection({ active: true, owned: true, reliable: true, processIds: [11], services: [] });
  await backend.execute("snapshot", {});
  // Release only the superseded re-check; the replacement tunnel's own check is still pending.
  state.gates[0].release({ verified: true, reason: "verified" });
  await flush();
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: not_checked\./);
  assert.equal((await backend.execute("snapshot", {})).connected, false);
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
});

test("cancel discards an in-flight re-check but keeps a healthy verdict", async () => {
  const { backend, state } = routeHarness();
  await backend.execute("connect", {});
  state.now += 31_000;
  await backend.execute("snapshot", {});
  await backend.execute("cancel", {});
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: verified\./);
  await backend.execute("snapshot", {});
  assert.equal(state.calls, 3, "the discarded completion starts a fresh re-check");
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
});

test("cancel aborts the in-flight background probes", async () => {
  const { backend, state } = routeHarness();
  await backend.execute("connect", {});
  state.now += 31_000;
  await backend.execute("snapshot", {});
  const pendingSignal = state.signals.at(-1);
  await backend.execute("cancel", {});
  assert.equal(pendingSignal.aborted, true, "the probe process is stopped, not left to time out");
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
});

test("connect still awaits the initial route proof", async () => {
  const proof = gate();
  const { backend, files, setInspection } = harness({
    start: async () => setInspection({ active: true, owned: true, reliable: true }),
    verifyRoute: async () => proof.promise,
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  let settled = false;
  const connecting = backend.execute("connect", {}).then(value => { settled = true; return value; });
  await flush();
  assert.equal(settled, false);
  assert.equal((await backend.execute("snapshot", {})).connected, false);
  proof.release({ verified: true, reason: "verified" });
  assert.equal((await connecting).success, true);
  assert.equal((await backend.execute("snapshot", {})).connected, true);
});

test("cancelling during the initial proof cleans up and discards the late verdict", async () => {
  const proof = gate();
  const { backend, calls, files, setInspection } = harness({
    start: async () => setInspection({ active: true, owned: true, reliable: true }),
    verifyRoute: async () => proof.promise,
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const connecting = backend.execute("connect", {});
  await flush();
  await backend.execute("cancel", {});
  proof.release({ verified: true, reason: "verified" });
  const result = await connecting;
  assert.equal(result.success, false);
  assert.ok(calls.some(c => c[0] === "stop"), "the owned tunnel is cleaned up");
  assert.ok(calls.some(c => c[0] === "launch"), "Discord is restored");
  await flush();
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: not_checked\./);
});

test("a verdict older than the cap never counts as connected", async () => {
  const { backend, state } = routeHarness();
  await backend.execute("connect", {});
  state.now += 31_000;
  await backend.execute("snapshot", {});
  state.now += 100_000;
  const stale = await backend.execute("snapshot", {});
  assert.equal(stale.connected, false);
  assert.equal(stale.readiness, "unverified");
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
});

test("a reconnect keeps the proof it just earned", async () => {
  let now = 1_000, pids = 0, proofs = 0;
  const { backend, files, setInspection } = harness({
    now: () => now,
    start: async () => { pids += 1; setInspection({ active: true, owned: true, reliable: true, processIds: [pids], services: [] }); },
    verifyRoute: async () => { proofs += 1; return { verified: true, reason: "verified" }; },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  assert.equal((await backend.execute("connect", {})).success, true);
  assert.equal((await backend.execute("snapshot", {})).connected, true);
  await backend.execute("disconnect", {});
  now += 1_000;
  assert.equal((await backend.execute("connect", {})).success, true);
  // No snapshot and no poll during the second connect: the new tunnel's identity must not
  // be mistaken for a replacement of the first one.
  const after = await backend.execute("snapshot", {});
  assert.equal(after.connected, true, "the fresh proof survives the first snapshot");
  assert.equal(proofs, 2);
});

test("a re-check that finishes after the tunnel died, with no snapshot in between, is discarded", async () => {
  const { backend, state, setInspection } = routeHarness();
  await backend.execute("connect", {});
  state.now += 31_000;
  await backend.execute("snapshot", {});
  // The tunnel dies with no snapshot to observe it: only the completion inspection can tell.
  setInspection({ active: false, owned: false, reliable: true });
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: not_checked\./);
});

test("a replaced tunnel with no snapshot in between cannot inherit the old proof", async () => {
  const { backend, state, setInspection } = routeHarness({
    start: async () => setInspection({ active: true, owned: true, reliable: true, processIds: [10], services: [] }),
  });
  await backend.execute("connect", {});
  state.now += 31_000;
  await backend.execute("snapshot", {});
  setInspection({ active: true, owned: true, reliable: true, processIds: [11], services: [] });
  state.gates.forEach(g => g.release({ verified: true, reason: "verified" }));
  await flush();
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: not_checked\./);
});

test("connect fails instead of reporting success when the tunnel dies before its proof lands", async () => {
  const proof = gate();
  const { backend, files, setInspection } = harness({
    start: async () => setInspection({ active: true, owned: true, reliable: true }),
    verifyRoute: async () => proof.promise,
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const connecting = backend.execute("connect", {});
  await flush();
  setInspection({ active: false, owned: false, reliable: true });
  proof.release({ verified: true, reason: "verified" });
  const result = await connecting;
  assert.equal(result.success, false);
  assert.match(result.message, /could not be started/i);
  await flush();
  assert.match((await backend.execute("diagnostics", {})).text, /Route check: not_checked\./);
});

test("waitForIdle joins cancelled work instead of acknowledging early", async () => {
  let release, began;
  const blocked = new Promise(r => release = r), started = new Promise(r => began = r);
  const { backend } = harness({ login: async () => { began(); await blocked; return { success: true }; } });
  const pending = backend.execute("login", { username: "fixture", password: "synthetic" });
  await started; await backend.execute("cancel", {});
  let joined = false;
  const idle = backend.execute("waitForIdle", {}).then(() => { joined = true; });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(joined, false);
  release(); await pending; await idle;
  assert.equal(joined, true);
});

test("cancellation keeps the mutation lock until owned cleanup has finished", async () => {
  let releaseStart, startBegan, releaseStop, stopBegan;
  const starting = new Promise(r => startBegan = r), blockedStart = new Promise(r => releaseStart = r);
  const stopping = new Promise(r => stopBegan = r), blockedStop = new Promise(r => releaseStop = r);
  const { backend, files, setInspection } = harness({
    start: async () => { startBegan(); await blockedStart; setInspection({ active: true, owned: true, reliable: true }); },
    stop: async () => { stopBegan(); await blockedStop; return { stopped: true }; },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const pending = backend.execute("connect", {});
  await starting; await backend.execute("cancel", {}); releaseStart(); await stopping;
  const overlap = backend.execute("disconnect", {});
  await new Promise(resolve => setImmediate(resolve));
  releaseStop(); await pending;
  const concurrent = await overlap;
  assert.equal(concurrent.success, false);
  assert.match(concurrent.message, /still running/);
});

test("disconnect closes Discord before removing the filter then confirms the normal relaunch", async () => {
  const { backend, calls, setInspection } = harness();
  setInspection({ active: true, owned: true, reliable: true });
  assert.equal((await backend.execute("disconnect", {})).success, true);
  assert.deepEqual(calls.filter(c => ["close-discord", "stop", "launch"].includes(c[0])).map(c => c[0]),
    ["close-discord", "stop", "launch"]);
});

test("a surviving owned WireSock process alone is never reported connected", async () => {
  const { backend, setInspection } = harness();
  setInspection({ active: true, owned: true, reliable: true });
  const snapshot = await backend.execute("snapshot", {});
  assert.equal(snapshot.connected, false);
  assert.equal(snapshot.tunnelActive, true);
  assert.equal(snapshot.readiness, "unverified");
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false, "an orphan process cannot short-circuit activation as a success");
});

test("snapshot exposes the exact contract and obtains username from the owned-session helper", async () => {
  const { backend } = harness();
  assert.deepEqual(await backend.execute("snapshot", {}), {
    connected: false,
    tunnelActive: false,
    readiness: "inactive",
    discordRunning: false,
    stage: "idle",
    externalTunnel: false,
    reliable: true,
    installerUnknown: false,
    inspectionReliable: true,
    signedIn: true,
    username: "own-user",
    route: null,
    mode: "proton",
    routeCheckedAgeMs: null,
    routeRechecking: false,
    hasOwnedTunnel: false,
    requiresElevation: false,
  });
});

test("mutations are gated by their actual effects, not by a blanket WireSock check", async () => {
  const external = { active: true, owned: false, reliable: true, reason: "GUI active" };
  const unreliable = { active: false, owned: false, reliable: false, reason: "unknown" };
  // Tunnel control stays fail-closed in both states.
  for (const blocked of [external, unreliable]) {
    for (const command of ["connect", "disconnect"]) {
      const { backend, calls, setInspection } = harness();
      setInspection(blocked);
      const result = await backend.execute(command, {});
      assert.equal(result.success, false, `${command} must stay closed`);
      assert.deepEqual(calls.filter(c => ["close-discord", "start", "stop", "launch"].includes(c[0])), []);
    }
  }
  // Sign-in and import touch no WireSock resource, so a foreign or unreadable tunnel must
  // not block them. This is what made an unelevated autostart useless.
  for (const allowed of [external, unreliable]) {
    const signingIn = harness();
    signingIn.setInspection(allowed);
    assert.equal((await signingIn.backend.execute("login", { username: "u", password: "p" })).success, true, "login");
    const importing = harness();
    importing.setInspection(allowed);
    assert.equal((await importing.backend.execute("importConfig", { path: String.raw`C:\picked\route.conf` })).success, true, "importConfig");
    // An invalid file is still refused on its own merits.
    importing.files.set("C:\\picked\\bad.conf", "not a wireguard profile");
    assert.equal((await importing.backend.execute("importConfig", { path: "C:\\picked\\bad.conf" })).success, false, "invalid import");
  }
  // A fresh, idle profile operation is allowed even when the state is unreadable.
  const idle = harness();
  idle.setInspection(unreliable);
  assert.equal((await idle.backend.execute("optimize", {})).success, true, "optimize");
  // Known native presence still blocks profile operations.
  const owned = harness();
  owned.setInspection({ active: true, owned: true, reliable: true });
  assert.equal((await owned.backend.execute("optimize", {})).success, false);
  assert.equal((await owned.backend.execute("logout", {})).success, false);
});

test("login propagates the selected human-verification method without persisting secrets", async () => {
  const { backend, calls, files } = harness();
  const result = await backend.execute("login", {
    username: "alice", password: "private", humanVerificationToken: "opaque", humanVerificationMethod: "ownership-email",
  });
  assert.equal(result.success, true);
  assert.equal(calls[0][2], "ownership-email");
  assert.equal([...files.values()].some(value => String(value).includes("private") || String(value).includes("opaque")), false);
});

test("cancel aborts an in-flight login and prevents stale success", async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  let staged;
  const started = new Promise(resolve => { staged = resolve; });
  const { backend } = harness({
    login: async (_d, _u, _p, _t, _h, operation) => {
      staged();
      await pending;
      return operation.isCurrent() ? { success: true } : { success: false, code: "CAPTCHA_CANCELLED", message: "cancelled" };
    },
  });
  const login = backend.execute("login", { username: "u", password: "p" });
  await started;
  assert.deepEqual(await backend.execute("cancel", {}), { success: true });
  release();
  assert.deepEqual(await login, { success: false, code: "CAPTCHA_CANCELLED", message: "Operation cancelled." });
});

test("mutating commands are serialized while cancel remains independently accepted", async () => {
  let release;
  let staged;
  const pending = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { staged = resolve; });
  const { backend } = harness({
    login: async () => { staged(); await pending; return { success: true }; },
  });
  const login = backend.execute("login", { username: "u", password: "p" });
  await started;
  assert.deepEqual(await backend.execute("optimize", {}), { success: false, message: "Another native backend operation is still running." });
  assert.deepEqual(await backend.execute("cancel", {}), { success: true });
  release();
  assert.equal((await login).success, false);
});

test("optimization uses own helper identity and freeOnly until a premium plan is known", async () => {
  const { backend, calls } = harness();
  const result = await backend.execute("optimize", { country: "nl" });
  assert.equal(result.success, true);
  const options = calls.find(call => call[0] === "generate")[1];
  assert.equal(options.username, "own-user");
  assert.equal(options.countries, "NL");
  assert.equal(options.freeOnly, true);
});

test("import validates an explicit absolute .conf and writes only sanitized native-owned data", async () => {
  const { backend, calls, files } = harness();
  const result = await backend.execute("importConfig", { path: "C:\\picked\\route.conf" });
  assert.deepEqual(result, { success: true });
  assert.match(files.get("C:\\native-data\\imported.conf"), /AllowedApps = C:\\Discord\\Discord\.exe/);
  assert.equal(calls.some(call => call[0] === "start" || call[0] === "stop"), false);
  assert.deepEqual(await backend.execute("importConfig", { path: "relative.conf" }), { success: false, message: "Select an absolute WireGuard .conf file." });
});

test("disconnect mutates only when exact native-owned config is active", async () => {
  const { backend, calls, setInspection } = harness();
  setInspection({ active: true, owned: true, reliable: true, reason: null });
  assert.deepEqual(await backend.execute("disconnect", {}), { success: true });
  assert.deepEqual(calls.find(call => call[0] === "stop"), ["stop", "C:\\native-data\\native-wiresock.conf"]);
});

test("cancel while generated state is being saved never continues to discovery or start", async () => {
  let releaseWrite;
  let writeStarted;
  const pendingWrite = new Promise(resolve => { releaseWrite = resolve; });
  const beganWrite = new Promise(resolve => { writeStarted = resolve; });
  const { backend, calls, files } = harness({
    generate: async (_dir, options) => {
      calls.push(["generate", options]);
      files.set("C:\\native-data\\wireguard.conf", goodConfig);
      return { success: true, server: "NL#1", country: "NL", pingMs: 34 };
    },
    files: {
      readExplicit: async p => files.get(p) ?? "",
      readOwned: async p => files.get(p) ?? "",
      writeOwned: async (p, value) => {
        calls.push(["write", p]);
        if (p.endsWith("state.json")) { writeStarted(); await pendingWrite; }
        files.set(p, value);
      },
      removeOwned: async p => files.delete(p),
      existsOwned: async p => files.has(p),
    },
  });
  const connect = backend.execute("connect", {});
  await beganWrite;
  assert.deepEqual(await backend.execute("cancel", {}), { success: true });
  releaseWrite();
  assert.equal((await connect).success, false);
  assert.equal(calls.some(call => call[0] === "start" || call[0] === "launch"), false);
  assert.equal(JSON.parse(files.get("C:\\native-data\\state.json")).route, null);
});

test("cancel during Discord discovery prevents profile read and start", async () => {
  let release;
  let began;
  const pending = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { began = resolve; });
  const { backend, calls, files } = harness({
    discoverDiscord: async () => { began(); await pending; return ["C:\\Discord\\Discord.exe"]; },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const connect = backend.execute("connect", {});
  await started;
  await backend.execute("cancel", {});
  release();
  assert.equal((await connect).success, false);
  assert.equal(calls.some(call => call[0] === "start"), false);
});

test("cancel during owned profile read prevents start", async () => {
  let release;
  let began;
  const pending = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { began = resolve; });
  const { backend, calls, files } = harness({
    files: {
      readExplicit: async p => files.get(p) ?? "",
      readOwned: async p => {
        if (p.endsWith("wireguard.conf")) { began(); await pending; }
        return files.get(p) ?? "";
      },
      writeOwned: async (p, value) => files.set(p, value),
      removeOwned: async p => files.delete(p),
      existsOwned: async p => files.has(p),
    },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const connect = backend.execute("connect", {});
  await started;
  await backend.execute("cancel", {});
  release();
  assert.equal((await connect).success, false);
  assert.equal(calls.some(call => call[0] === "start"), false);
});

test("cancel during start restores Discord only after exact owned tunnel cleanup", async () => {
  let releaseStart;
  let startBegan;
  const pendingStart = new Promise(resolve => { releaseStart = resolve; });
  const beganStart = new Promise(resolve => { startBegan = resolve; });
  let inspection = { active: false, owned: false, reliable: true };
  const { backend, calls, files } = harness({
    inspect: async () => inspection,
    start: async () => { calls.push(["start"]); startBegan(); await pendingStart; inspection = { active: true, owned: true, reliable: true }; },
    stop: async p => { calls.push(["stop", p]); inspection = { active: false, owned: false, reliable: true }; return { stopped: true }; },
    launchDiscord: async () => { assert.equal(inspection.active, false); calls.push(["launch"]); },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const connect = backend.execute("connect", {});
  await beganStart;
  await backend.execute("cancel", {});
  releaseStart();
  assert.equal((await connect).success, false);
  assert.deepEqual(calls.filter(call => call[0] === "stop"), [["stop", "C:\\native-data\\native-wiresock.conf"]]);
  assert.equal(calls.some(call => call[0] === "launch"), true);
});

test("logout removes Proton credentials and generated runtime profiles but preserves imported custom profile", async () => {
  const { backend, calls, files } = harness();
  files.set("C:\\native-data\\proton-session.json", "session");
  files.set("C:\\native-data\\proton-session.json.lock", "lock");
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  files.set("C:\\native-data\\native-wiresock.conf", goodConfig);
  files.set("C:\\native-data\\imported.conf", goodConfig);
  files.set("C:\\native-data\\state.json", JSON.stringify({ version: 1, mode: "proton", route: { server: "NL#1", country: "NL" } }));
  assert.deepEqual(await backend.execute("logout", {}), { success: true });
  assert.equal(files.has("C:\\native-data\\proton-session.json"), false);
  assert.equal(files.has("C:\\native-data\\wireguard.conf"), false);
  assert.equal(files.has("C:\\native-data\\native-wiresock.conf"), false);
  assert.equal(files.has("C:\\native-data\\imported.conf"), true);
  assert.equal(JSON.parse(files.get("C:\\native-data\\state.json")).route, null);
  assert.equal(calls.some(call => call[0] === "start"), false);
});

test("signed-out Proton mode cannot reuse a stale generated profile", async () => {
  const { backend, calls, files } = harness({ getSavedSessionUsername: async () => "" });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.match(result.message, /Sign in/i);
  assert.equal(calls.some(call => call[0] === "start"), false);
});

test("helper messages cannot expose arbitrary account, secret, URL query, path, or key material", async () => {
  const username = "person@example.test";
  const password = "fixture-password-293";
  const key = "K".repeat(44);
  const hostile = `helper failed for ${username} ${password} at C:\\private\\person.conf https://vpn-api.proton.me/core/v4/captcha?Token=fixture-query-937 PrivateKey = ${key}`;
  const { backend } = harness({ login: async () => ({ success: false, code: "NETWORK_ERROR", message: hostile, error: hostile,
    route: { server: "fixture-secret-725", country: "NL" },
    captchaUrl: "https://verify.proton.me/?token=fixture-query-938" }) });
  const result = await backend.execute("login", { username, password });
  assert.equal(result.code, "NETWORK_ERROR");
  assert.equal(result.success, false);
  for (const sensitive of [username, password, "fixture-query-937", "fixture-query-938", "person.conf", key, "fixture-secret-725"]) {
    assert.equal(JSON.stringify(result).includes(sensitive), false, sensitive);
  }
  assert.match(result.message, /network|connection|Proton/i);
});

test("thrown helper errors and invalid-profile details stay out of backend responses", async () => {
  const hostile = "C:\\private\\person.conf person@example.test https://proton.me/captcha?Token=fixture-query-937 PrivateKey=fixture-key-482";
  const optimize = harness({ generate: async () => { throw new Error(hostile); } });
  const optimization = await optimize.backend.execute("optimize", {});
  assert.equal(optimization.success, false);
  assert.equal(JSON.stringify(optimization).includes("fixture-query-937"), false);
  const imported = harness({ validateConfig: () => ({ valid: false, error: hostile }) });
  const validation = await imported.backend.execute("importConfig", { path: "C:\\picked\\route.conf" });
  assert.equal(validation.success, false);
  assert.equal(JSON.stringify(validation).includes("person.conf"), false);
});

test("security challenge retains its explicit Proton URL and safe action text", async () => {
  const challenge = "https://verify.proton.me/?token=fixture-challenge&methods=ownership-email&embed=1&vpn=1";
  const { backend } = harness({ login: async () => ({ success: false, code: "CAPTCHA_REQUIRED",
    error: "unexpected account detail person@example.test", captchaUrl: challenge }) });
  const result = await backend.execute("login", { username: "person@example.test", password: "fixture-password-293" });
  assert.equal(result.code, "CAPTCHA_REQUIRED");
  assert.equal(result.captchaUrl, challenge);
  assert.match(result.message, /security check/i);
  assert.equal(result.message.includes("person@example.test"), false);
});

for (const restored of [true, false]) {
  test(`WireSock setup guidance reaches the connect response (cleanup=${restored})`, async () => {
    const error = new WireSockSetupError(false, [String.raw`D:\Programas\Wiresock`]);
    error.message = "synthetic-private-path-and-token";
    const { backend, files } = harness({
      start: async () => { throw error; },
      launchDiscord: async () => { if (!restored) throw new Error("fixture restoration failed"); },
    });
    files.set("C:\\native-data\\wireguard.conf", goodConfig);
    const result = await backend.execute("connect", {});
    assert.equal(result.success, false);
    assert.equal(result.code, "WIRESOCK_SETUP_REQUIRED");
    assert.match(result.message, /WireSock Secure Connect SDK/);
    assert.match(result.message, /https:\/\/wiresock.net\/wiresock-sdk/);
    assert.match(result.message, /standalone.*GUI/);
    assert.ok(result.message.includes(String.raw`D:\Programas\Wiresock`));
    assert.match(result.message, /terms.*Windows.*prompt/);
    assert.doesNotMatch(result.message, /synthetic-private/);
    assert.equal(result.message.includes("could not confirm"), !restored);
  });
}

test("WireSock setup cancellation keeps guidance and does not disclose vendor errors", () => {
  const result = wireSockSetupFailure(new WireSockSetupError(true));
  assert.match(result.message, /cancelled/);
  assert.match(result.message, /WireSock Secure Connect SDK/);
});

// Verbatim shape of the Proton confgen template, including its metadata header comments.
const protonShaped = `# ProtonVPN WireGuard Configuration
# Generated: 2026-01-01T00:00:00Z
# Device: Brisa
#
# Server Information:
# - Name: NL#1
# - Country: NL
# - Tier: Free
# Physical Server:
# - Entry IP: 192.0.2.1
#
[Interface]
PrivateKey = ${"A".repeat(43)}=
Address = 10.2.0.2/32, 2a07:b944::2:2/128
DNS = 10.2.0.1, 2a07:b944::2:1

[Peer]
PublicKey = ${"B".repeat(43)}=
AllowedIPs = 0.0.0.0/0, ::/0
Endpoint = 192.0.2.1:51820
PersistentKeepalive = 10
`;

test("a real Proton-shaped profile still connects end to end", async () => {
  const { backend, calls, files, setInspection } = harness({
    validateConfig: validateWireGuardConfig,
    sanitizeConfig: sanitizeWireGuardConfig,
    start: async (...args) => { calls.push(["start", ...args]); setInspection({ active: true, owned: true, reliable: true }); },
  });
  files.set("C:\\native-data\\wireguard.conf", protonShaped);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, true);
  assert.deepEqual(calls.filter(c => ["close-discord", "start", "launch"].includes(c[0])).map(c => c[0]),
    ["close-discord", "start", "launch"]);
  const started = calls.find(c => c[0] === "start")[2];
  assert.equal(validateWireGuardConfig(started).valid, true, "the raw profile handed to the elevated client is valid");
});

test("importing a profile with a PostUp hook is rejected before anything is written or closed", async () => {
  const { backend, calls, files } = harness({
    validateConfig: validateWireGuardConfig,
    sanitizeConfig: sanitizeWireGuardConfig,
  });
  files.set("C:\\picked\\hooked.conf", protonShaped.replace("[Peer]", "[Peer]\nPostUp = calc.exe"));
  const result = await backend.execute("importConfig", { path: "C:\\picked\\hooked.conf" });
  assert.equal(result.success, false);
  assert.equal(result.code, "PROFILE_HOOKS");
  assert.match(result.message, /PreUp or PostUp/);
  assert.doesNotMatch(result.message, /calc\.exe/);
  assert.deepEqual(calls.filter(c => ["write", "close-discord", "start", "stop"].includes(c[0])), []);
});

test("a legacy saved profile with a hook is rejected at connect before Discord is touched", async () => {
  const { backend, calls, files } = harness({
    validateConfig: validateWireGuardConfig,
    sanitizeConfig: sanitizeWireGuardConfig,
  });
  files.set("C:\\native-data\\state.json", JSON.stringify({ version: 1, mode: "custom", route: null }));
  files.set("C:\\native-data\\imported.conf", protonShaped.replace("[Peer]", "[Peer]\nPostUp = calc.exe"));
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.equal(result.code, "PROFILE_HOOKS");
  assert.match(result.message, /import it again/i);
  assert.deepEqual(calls.filter(c => ["close-discord", "start", "stop", "launch"].includes(c[0])), []);
});

test("a refused start reports its own reason and records it", async () => {
  const diag = createDiagnostics();
  const { backend, calls, files } = harness({
    diag,
    start: async () => { throw new WireSockStartError("START_EXTERNAL_SERVICE"); },
  });
  files.set(String.raw`C:\native-data\wireguard.conf`, goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.equal(result.code, "START_EXTERNAL_SERVICE");
  assert.match(result.message, /Another app's WireSock service/);
  assert.doesNotMatch(result.message, /could not confirm tunnel cleanup/);
  assert.ok(calls.some(c => c[0] === "launch"), "the normal route is restored");
  assert.ok(diag.recent().some(line => line.includes("START_EXTERNAL_SERVICE")));
});

test("a coded start failure keeps its code when cleanup could not be confirmed", async () => {
  const diag = createDiagnostics();
  const { backend, files, setInspection } = harness({
    diag,
    start: async () => { setInspection({ active: true, owned: true, reliable: true }); throw new WireSockStartError("START_CLIENT_EXITED", { exitCode: 1 }); },
    stop: async () => ({ stopped: false }),
  });
  files.set(String.raw`C:\native-data\wireguard.conf`, goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.code, "START_CLIENT_EXITED");
  assert.match(result.message, /could not confirm tunnel cleanup/);
  assert.doesNotMatch(result.message, /restored the normal connection/);
  assert.ok(diag.recent().some(line => line.includes("STOP_NOT_CONFIRMED")));
  assert.ok(diag.recent().some(line => line.includes("START_CLIENT_EXITED exitCode=1")));
});

test("an unclassified start error keeps the generic message and carries no code", async () => {
  const diag = createDiagnostics();
  const { backend, files } = harness({ diag, start: async () => { throw new Error("synthetic failure"); } });
  files.set(String.raw`C:\native-data\wireguard.conf`, goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.equal(result.code, undefined);
  assert.match(result.message, /The Brisa tunnel could not be started/);
  assert.ok(diag.recent().some(line => line.includes("START_FAILED_UNCLASSIFIED")));
  assert.doesNotMatch(JSON.stringify(diag.recent()), /synthetic failure/);
});

test("the diagnostics command appends recent events only when there are any", async () => {
  const diag = createDiagnostics();
  const { backend } = harness({ diag });
  const clean = (await backend.execute("diagnostics", {})).text;
  assert.match(clean, /^WireSock inspection:/);
  assert.match(clean.trim().split("\n").at(-1), /^Packaged Proton helper:/);
  assert.doesNotMatch(clean, /Recent events/);
  diag.record("startup", "NOT_ELEVATED");
  const withEvents = (await backend.execute("diagnostics", {})).text;
  assert.match(withEvents, /Recent events \(this session\):/);
  assert.match(withEvents, /NOT_ELEVATED/);
  // Without a diagnostics dependency the seven original lines are all there is.
  const plain = (await harness().backend.execute("diagnostics", {})).text;
  assert.doesNotMatch(plain, /Recent events/);
});

test("a plan lookup failure is recorded and still falls back to free servers", async () => {
  const diag = createDiagnostics();
  const { backend, calls } = harness({ diag, getPlan: async () => { throw new Error("synthetic network failure"); } });
  const result = await backend.execute("optimize", {});
  assert.equal(result.success, true);
  assert.equal(calls.find(c => c[0] === "generate")[1].freeOnly, true);
  assert.ok(diag.recent().some(line => line.includes("PLAN_LOOKUP_FAILED")));
  assert.doesNotMatch(JSON.stringify(diag.recent()), /synthetic network failure/);
});

test("a rejected profile is recorded with its own code", async () => {
  const diag = createDiagnostics();
  const { backend, files } = harness({ diag, validateConfig: validateWireGuardConfig, sanitizeConfig: sanitizeWireGuardConfig });
  files.set(String.raw`C:\picked\hooked.conf`, protonShaped.replace("[Peer]", "[Peer]\nPostUp = calc.exe"));
  const result = await backend.execute("importConfig", { path: String.raw`C:\picked\hooked.conf` });
  assert.equal(result.code, "PROFILE_HOOKS");
  assert.ok(diag.recent().some(line => line.includes("preparing-profile PROFILE_HOOKS")));
});

test("a mixed snapshot reports owned presence without ever reporting connected", async () => {
  let probes = 0, useOther = false;
  const { backend, calls, files, setInspection } = harness({
    start: async (...args) => { calls.push(["start", ...args]); setInspection({ active: true, owned: true, reliable: true, processIds: [10], services: [] }); },
    verifyRoute: async () => { probes += 1; return { verified: true, reason: "verified" }; },
    discoverDiscord: async () => [useOther ? String.raw`C:\Other\Discord.exe` : String.raw`C:\Session\Discord.exe`],
  });
  files.set(String.raw`C:\native-data\wireguard.conf`, goodConfig);
  assert.equal((await backend.execute("connect", {})).success, true);
  assert.equal(probes, 1);
  useOther = true;
  setInspection({ active: true, owned: false, reliable: true, reason: "mixed", services: ["wiresock-client-service", "other"], processIds: [10, 99], ownServices: ["wiresock-client-service"], ownProcessIds: [10] });
  const mixed = await backend.execute("snapshot", {});
  assert.equal(mixed.tunnelActive, false);
  assert.equal(mixed.connected, false);
  assert.equal(mixed.hasOwnedTunnel, true);
  assert.equal(mixed.externalTunnel, true);
  assert.equal(probes, 1, "no route proof while ownership is mixed");
  // Mixed disconnect is refused before Discord is touched.
  const touchedBefore = calls.filter(c => ["close-discord", "stop"].includes(c[0])).length;
  const refused = await backend.execute("disconnect", {});
  assert.equal(refused.success, false);
  assert.match(refused.message, /still running alongside another WireSock tunnel/);
  assert.equal(calls.filter(c => ["close-discord", "stop"].includes(c[0])).length, touchedBefore);
  // Once the other tunnel is gone, the retained session apps are used for the real disconnect.
  setInspection({ active: true, owned: true, reliable: true, processIds: [10], services: [] });
  assert.equal((await backend.execute("disconnect", {})).success, true);
  assert.deepEqual(calls.find(c => c[0] === "close-discord")[1], [String.raw`C:\Session\Discord.exe`]);
});

test("unreliable inspection with a positively identified native process still reports presence", async () => {
  const { backend, setInspection } = harness();
  setInspection({ active: false, owned: false, reliable: false, ownProcessIds: [10] });
  const snapshot = await backend.execute("snapshot", {});
  assert.equal(snapshot.hasOwnedTunnel, true);
  assert.equal(snapshot.tunnelActive, false);
  assert.equal(snapshot.connected, false);
});

test("signing out without reliable ownership keeps the native profile and says so", async () => {
  const { backend, files, setInspection } = harness();
  files.set(String.raw`C:\native-data\native-wiresock.conf`, "sentinel");
  setInspection({ active: false, owned: false, reliable: false });
  const result = await backend.execute("logout", {});
  assert.equal(result.success, true);
  assert.match(result.message, /kept its native tunnel profile/);
  assert.equal(files.get(String.raw`C:\native-data\native-wiresock.conf`), "sentinel");
  // A later reliably inactive sign-out finishes clearing it.
  setInspection({ active: false, owned: false, reliable: true });
  const again = await backend.execute("logout", {});
  assert.equal(again.success, true);
  assert.equal(again.message, undefined);
  assert.equal(files.has(String.raw`C:\native-data\native-wiresock.conf`), false);
});

test("the start reports installing-wiresock before starting-tunnel", async () => {
  let backend;
  const seen = [];
  let capturedStage = null;
  const h = harness({
    start: async (configPath, raw, apps, signal, options) => {
      capturedStage = options?.onStage;
      options?.onStage?.("installing-wiresock");
      seen.push((await backend.execute("progress", {})).stage);
      options?.onStage?.("starting-tunnel");
      seen.push((await backend.execute("progress", {})).stage);
      h.setInspection({ active: true, owned: true, reliable: true });
    },
    verifyRoute: async () => ({ verified: true, reason: "verified" }),
  });
  backend = h.backend;
  h.files.set(String.raw`C:\native-data\wireguard.conf`, goodConfig);
  assert.equal((await backend.execute("connect", {})).success, true);
  assert.deepEqual(seen, ["installing-wiresock", "starting-tunnel"]);
  // A callback from a finished operation must not repaint the stage of the next one.
  capturedStage?.("installing-wiresock");
  assert.equal((await backend.execute("progress", {})).stage, "idle");
});

test("a lost installer supervision is never reported as a cancellation", async () => {
  let backend;
  const h = harness({
    start: async () => {
      await backend.execute("cancel", {});
      const error = new Error("supervision lost");
      error.name = "InstallerSupervisionLost";
      throw error;
    },
  });
  backend = h.backend;
  h.files.set(String.raw`C:\native-data\wireguard.conf`, goodConfig);
  const result = await backend.execute("connect", {});
  assert.equal(result.success, false);
  assert.equal(result.code, "SDK_INSTALL_SUPERVISION_LOST", "supervision loss must not degrade to a cancellation");
  assert.doesNotMatch(result.message, /cancelled/i);
  const snapshot = await backend.execute("snapshot", {});
  assert.equal(snapshot.reliable, false, "the state must stay unreliable so ExitGuard keeps the app open");
  assert.equal(snapshot.installerUnknown, true);
  assert.notEqual((await backend.execute("progress", {})).stage, "idle");
  const idle = await backend.execute("waitForIdle", {});
  assert.equal(idle.success, false);
  assert.equal(idle.code, "SDK_INSTALL_SUPERVISION_LOST");
});

test("a waitForIdle already pending is refused when supervision is lost", async () => {
  let backend;
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  const h = harness({
    start: async () => {
      await gate;
      const error = new Error("supervision lost");
      error.name = "InstallerSupervisionLost";
      throw error;
    },
  });
  backend = h.backend;
  h.files.set(String.raw`C:\native-data\wireguard.conf`, goodConfig);
  const connecting = backend.execute("connect", {});
  await new Promise(resolve => setTimeout(resolve, 10));
  const waiting = backend.execute("waitForIdle", {});
  release();
  await connecting;
  const idle = await waiting;
  assert.equal(idle.success, false, "a caller already waiting must not be told the backend is idle");
  assert.equal(idle.code, "SDK_INSTALL_SUPERVISION_LOST");
});

test("a disconnect failure says whether Discord was actually closed", async () => {
  const closedMessage = "Discord was closed, but the native-owned tunnel could not be stopped safely. Brisa will stay open. Check the tunnel status before reopening Discord.";
  const stopFailed = harness({ stop: async () => ({ stopped: false }) });
  stopFailed.setInspection({ active: true, owned: true, reliable: true });
  const first = await stopFailed.backend.execute("disconnect", {});
  assert.equal(first.message, closedMessage);
  assert.equal(stopFailed.calls.some(c => c[0] === "launch"), false);

  const diag = createDiagnostics();
  const stopThrew = harness({ diag, stop: async () => { throw new Error("synthetic stop failure"); } });
  stopThrew.setInspection({ active: true, owned: true, reliable: true });
  const second = await stopThrew.backend.execute("disconnect", {});
  assert.equal(second.message, closedMessage);
  assert.equal(diag.recent().filter(line => line.includes("STOP_NOT_CONFIRMED")).length, 1);
  assert.doesNotMatch(JSON.stringify(diag.recent()), /synthetic stop failure/);

  const noApps = harness({ discoverDiscord: async () => [], stop: async () => ({ stopped: false }) });
  noApps.setInspection({ active: true, owned: true, reliable: true });
  const third = await noApps.backend.execute("disconnect", {});
  assert.match(third.message, /^The native-owned tunnel could not be stopped safely/);
  assert.doesNotMatch(third.message, /Discord was closed/);

  const closeThrew = harness({ stopDiscord: async () => { throw new Error("synthetic close failure"); } });
  closeThrew.setInspection({ active: true, owned: true, reliable: true });
  const fourth = await closeThrew.backend.execute("disconnect", {});
  assert.match(fourth.message, /Discord or the tunnel could not be stopped safely/);
  assert.doesNotMatch(fourth.message, /Discord was closed/);
});

test("session ownership survives a completely unreadable inspection", async () => {
  const { backend, files, setInspection } = harness({
    start: async () => setInspection({ active: true, owned: true, reliable: true, processIds: [10], services: [] }),
    verifyRoute: async () => ({ verified: true, reason: "verified" }),
  });
  files.set(String.raw`C:\native-data\wireguard.conf`, goodConfig);
  assert.equal((await backend.execute("connect", {})).success, true);
  // A PowerShell timeout or a null snapshot proves nothing about a tunnel this session started.
  setInspection({ active: false, owned: false, reliable: false, ownProcessIds: [], ownServices: [] });
  const snapshot = await backend.execute("snapshot", {});
  assert.equal(snapshot.hasOwnedTunnel, true);
  assert.equal(snapshot.connected, false);
  assert.equal((await backend.execute("logout", {})).success, false);
  assert.equal((await backend.execute("optimize", {})).success, false);
  // Only a reliable inspection that shows nothing clears it.
  setInspection({ active: false, owned: false, reliable: true });
  const cleared = await backend.execute("snapshot", {});
  assert.equal(cleared.hasOwnedTunnel, false);
  assert.equal((await backend.execute("optimize", {})).success, true);
});
