import assert from "node:assert/strict";
import test from "node:test";

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
  const { backend, calls, files } = harness({
    discoverDiscord: async () => [current],
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
  const { backend, calls, files } = harness();
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

test("status rechecks route health instead of keeping a stale verified badge", async () => {
  let now = 1_000, healthy = true, probes = 0;
  const { backend, files, setInspection } = harness({
    now: () => now,
    start: async () => setInspection({ active: true, owned: true, reliable: true }),
    verifyRoute: async () => { probes++; return { verified: healthy, reason: healthy ? "verified" : "probe_failed" }; },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  await backend.execute("connect", {});
  assert.equal((await backend.execute("snapshot", {})).connected, true);
  healthy = false; now += 31_000;
  assert.equal((await backend.execute("snapshot", {})).connected, false);
  assert.equal(probes, 2);
  assert.equal((await backend.execute("snapshot", {})).tunnelActive, true);
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
    signedIn: true,
    username: "own-user",
    route: null,
    mode: "proton",
  });
});

test("every mutation fails closed for external or unreliable WireSock inspection", async () => {
  for (const blocked of [
    { active: true, owned: false, reliable: true, reason: "GUI active" },
    { active: false, owned: false, reliable: false, reason: "unknown" },
  ]) {
    const { backend, calls, setInspection } = harness();
    setInspection(blocked);
    for (const [command, payload] of [
      ["login", { username: "u", password: "p" }], ["logout", {}], ["optimize", {}],
      ["connect", {}], ["disconnect", {}], ["importConfig", { path: "C:\\picked\\route.conf" }],
    ]) {
      const result = await backend.execute(command, payload);
      assert.equal(result.success, false, command);
    }
    assert.equal(calls.length, 0);
  }
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
