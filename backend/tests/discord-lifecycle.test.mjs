import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import test from "node:test";

import { createDiscordLifecycle, discordAllowedApps } from "../src/discord-lifecycle.mjs";

const APP = String.raw`C:\Users\fixture\AppData\Local\Discord\app-1.2.3\Discord.exe`;
const OLD = String.raw`C:\Users\fixture\AppData\Local\Discord\app-1.2.2\Discord.exe`;
const UPDATE = String.raw`C:\Users\fixture\AppData\Local\Discord\Update.exe`;
const OTHER_UPDATE = String.raw`C:\Unrelated\Update.exe`;

function fixture(initial = []) {
  let processes = initial.map(value => ({ ...value }));
  const killed = [];
  const execFile = (_file, args, _options, callback) => {
    const script = args[args.indexOf("-Command") + 1];
    if (script.includes("ConvertTo-Json")) return queueMicrotask(() => callback(null, JSON.stringify(processes), ""));
    const pid = Number(/-TargetPid (\d+)/.exec(script)?.[1]);
    killed.push(pid);
    processes = processes.filter(process => process.ProcessId !== pid);
    queueMicrotask(() => callback(null, "", ""));
  };
  return { execFile, killed, processes: () => processes };
}

function childThat(error) {
  const child = new EventEmitter();
  queueMicrotask(() => error ? child.emit("error", error) : child.emit("spawn"));
  return child;
}

test("discordAllowedApps uses Windows paths and scopes Update.exe to each install root", () => {
  const allowed = discordAllowedApps([APP, APP.toUpperCase()]);
  assert.deepEqual(allowed.executables, [APP]);
  assert.deepEqual(allowed.executableNames, ["Discord.exe"]);
  assert.deepEqual(allowed.appDirs, [String.raw`C:\Users\fixture\AppData\Local\Discord\app-1.2.3`]);
  assert.deepEqual(allowed.updaterPaths, [UPDATE]);
});

test("an updater alone is not a running Discord client", async () => {
  const f = fixture([{ ProcessId: 12, ExecutablePath: UPDATE, Name: "Update.exe" }]);
  assert.equal(await createDiscordLifecycle({ execFile: f.execFile }).isRunning([APP]), false);
});

test("process control commands have deadlines and recheck the path at termination", async () => {
  const f = fixture([{ ProcessId: 10, ExecutablePath: APP, Name: "Discord.exe" }]);
  const seen = [];
  await createDiscordLifecycle({ execFile: (file, args, options, cb) => { seen.push({ args, options }); return f.execFile(file, args, options, cb); }, sleep: async () => {} }).stop([APP]);
  assert.ok(seen.every(item => item.options.timeout > 0));
  assert.ok(seen.some(item => item.args.some(arg => arg.includes("$p.Kill()") && arg.includes("MainModule.FileName") && arg.includes("ExpectedPath"))));
});

test("stop closes exact, older-version, and owned updater processes but leaves unrelated updater", async () => {
  const f = fixture([
    { ProcessId: 10, ExecutablePath: APP, Name: "Discord.exe" },
    { ProcessId: 11, ExecutablePath: OLD, Name: "Discord.exe" },
    { ProcessId: 12, ExecutablePath: UPDATE, Name: "Update.exe" },
    { ProcessId: 13, ExecutablePath: OTHER_UPDATE, Name: "Update.exe" },
  ]);
  const lifecycle = createDiscordLifecycle({ execFile: f.execFile, sleep: async () => {}, stableChecks: 2 });
  await lifecycle.stop([APP]);
  assert.deepEqual(f.killed.sort((a, b) => a - b), [10, 11, 12]);
  assert.deepEqual(f.processes().map(p => p.ProcessId), [13]);
});

test("an unreadable generic updater is not treated as a Discord process by name", async () => {
  const f = fixture([{ ProcessId: 70, ParentProcessId: 99, ExecutablePath: null, Name: "Update.exe" }]);
  await createDiscordLifecycle({ execFile: f.execFile, sleep: async () => {} }).stop([APP]);
  assert.deepEqual(f.killed, []);
});
test("an unreadable child updater of an owned Discord process fails closed", async () => {
  const f = fixture([{ ProcessId: 10, ExecutablePath: APP, Name: "Discord.exe" },
    { ProcessId: 70, ParentProcessId: 10, ExecutablePath: null, Name: "Update.exe" }]);
  await assert.rejects(createDiscordLifecycle({ execFile: f.execFile }).stop([APP]), /ownership/i);
  assert.deepEqual(f.killed, []);
});

test("stop fails closed when a matching candidate has no readable executable path", async () => {
  const f = fixture([{ ProcessId: 14, ExecutablePath: null, Name: "Discord.exe" }]);
  const lifecycle = createDiscordLifecycle({ execFile: f.execFile, sleep: async () => {}, maxAttempts: 2 });
  await assert.rejects(lifecycle.stop([APP]), /ownership/i);
  assert.deepEqual(f.killed, []);
});

test("launch closes before spawn and confirms the requested main path", async () => {
  let running = [{ ProcessId: 1, ExecutablePath: APP, Name: "Discord.exe" }];
  const events = [];
  const execFile = (_file, args, _options, callback) => {
    const script = args[args.indexOf("-Command") + 1];
    if (script.includes("ConvertTo-Json")) return queueMicrotask(() => callback(null, JSON.stringify(running), ""));
    events.push("kill"); running = [];
    queueMicrotask(() => callback(null, "", ""));
  };
  const spawn = (exe, args) => {
    assert.deepEqual(args, [APP]);
    events.push(`spawn:${exe}`);
    running = [{ ProcessId: 2, ExecutablePath: APP, Name: "Discord.exe" }];
    return childThat();
  };
  await createDiscordLifecycle({ execFile, spawn, sleep: async () => {}, stableChecks: 1 }).launch([APP]);
  assert.deepEqual(events, ["kill", "spawn:explorer.exe"]);
});

test("launch propagates spawn errors", async () => {
  const f = fixture([]);
  const lifecycle = createDiscordLifecycle({ execFile: f.execFile, spawn: () => childThat(new Error("spawn failed")), sleep: async () => {}, stableChecks: 1 });
  await assert.rejects(lifecycle.launch([APP]), /spawn failed/);
});

test("launch honors cancellation and never spawns", async () => {
  const f = fixture([]);
  const controller = new AbortController(); controller.abort();
  let spawned = false;
  const lifecycle = createDiscordLifecycle({ execFile: f.execFile, spawn: () => { spawned = true; return childThat(); }, sleep: async () => {} });
  await assert.rejects(lifecycle.launch([APP], controller.signal), error => error.name === "AbortError");
  assert.equal(spawned, false);
});

test("multi-process shutdown uses one checked batch instead of per-PID shells", async () => {
  let processes = Array.from({ length: 8 }, (_, i) => ({ ProcessId: i + 10, ExecutablePath: APP, Name: "Discord.exe" }));
  let elapsed = 0, killCalls = 0;
  const execFile = (_file, args, _options, cb) => {
    elapsed += 900;
    const script = args[args.indexOf("-Command") + 1];
    if (script.includes("ConvertTo-Json")) return queueMicrotask(() => cb(null, JSON.stringify(processes)));
    killCalls += 1;
    const targets = [...script.matchAll(/-TargetPid (\d+)/g)].map(m => Number(m[1]));
    processes = processes.filter(p => !targets.includes(p.ProcessId));
    queueMicrotask(() => cb(null, ""));
  };
  await createDiscordLifecycle({ execFile, clock: () => elapsed, sleep: async ms => { elapsed += ms; } }).stop([APP]);
  assert.equal(killCalls, 1);
  assert.equal(processes.length, 0);
  assert.ok(elapsed < 12_000);
});

test("running client selection excludes updater-only and unused installations", async () => {
  const f = fixture([{ ProcessId: 10, ExecutablePath: OLD, Name: "Discord.exe" },
    { ProcessId: 20, ExecutablePath: String.raw`C:\Unrelated\Update.exe`, Name: "Update.exe" }]);
  assert.deepEqual(await createDiscordLifecycle({ execFile: f.execFile }).runningApps([APP, String.raw`C:\Unused\Equibop.exe`]), [APP]);
});

test("default startup budget allows a client that appears after three seconds", async () => {
  let time = 0;
  let spawnedAt = null;
  const execFile = (_file, _args, _options, cb) => queueMicrotask(() => cb(null, JSON.stringify(
    spawnedAt !== null && time - spawnedAt >= 3_000 ? [{ ProcessId: 10, ExecutablePath: APP, Name: "Discord.exe" }] : [])));
  await createDiscordLifecycle({ execFile, spawn: () => { spawnedAt = time; return childThat(); },
    clock: () => time, sleep: async ms => { time += ms; } }).launch([APP]);
  assert.ok(time >= 3_000);
  assert.ok(time < 12_500);
});

test("launch confirmation is bounded when the process never appears", async () => {
  const f = fixture([]);
  let sleeps = 0;
  const lifecycle = createDiscordLifecycle({ execFile: f.execFile, spawn: () => childThat(), sleep: async () => { sleeps += 1; }, maxAttempts: 3, stableChecks: 1 });
  await assert.rejects(lifecycle.launch([APP]), /did not start/i);
  assert.equal(sleeps, 2);
});
