import assert from "node:assert/strict";
import test from "node:test";

import { createBackend } from "../src/backend.mjs";

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
    stop: async configPath => { calls.push(["stop", configPath]); return { stopped: true }; },
    validateConfig: raw => raw === goodConfig ? { valid: true } : { valid: false, error: "invalid profile" },
    sanitizeConfig: (raw, allowed) => `${raw}#@ws:AllowedApps = ${allowed}`,
    discoverDiscord: async () => ["C:\\Discord\\Discord.exe"],
    launchDiscord: async exe => { calls.push(["launch", exe]); },
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

test("snapshot exposes the exact contract and obtains username from the owned-session helper", async () => {
  const { backend } = harness();
  assert.deepEqual(await backend.execute("snapshot", {}), {
    connected: false,
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

test("cancel during start cleans up only an exact native-owned tunnel and never launches Discord", async () => {
  let releaseStart;
  let startBegan;
  const pendingStart = new Promise(resolve => { releaseStart = resolve; });
  const beganStart = new Promise(resolve => { startBegan = resolve; });
  let inspection = { active: false, owned: false, reliable: true };
  const { backend, calls, files } = harness({
    inspect: async () => inspection,
    start: async () => { calls.push(["start"]); startBegan(); await pendingStart; inspection = { active: true, owned: true, reliable: true }; },
  });
  files.set("C:\\native-data\\wireguard.conf", goodConfig);
  const connect = backend.execute("connect", {});
  await beganStart;
  await backend.execute("cancel", {});
  releaseStart();
  assert.equal((await connect).success, false);
  assert.deepEqual(calls.filter(call => call[0] === "stop"), [["stop", "C:\\native-data\\native-wiresock.conf"]]);
  assert.equal(calls.some(call => call[0] === "launch"), false);
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
