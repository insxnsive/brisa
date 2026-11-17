import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
import { powerShellCommandArgs, quotePowerShell } from "../src/network/powershell.ts";
import { transformSync } from "esbuild";

const source = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../src/native-wiresock-start.ts"), "utf8");
const production = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../src/production.ts"), "utf8");

test("production blocks mutations when the current Windows token is not elevated", () => {
  assert.ok(production.includes("let elevated = false"));
  // Elevation now uses the locale-independent integrity SID, avoiding PowerShell startup.
  assert.ok(production.includes('execFileSync("whoami.exe", ["/groups", "/fo", "csv", "/nh"]'));
  assert.doesNotMatch(production, /WindowsBuiltInRole/);
  assert.ok(production.includes("elevated ? inspectWireSockAsync(configPath)"));
  assert.ok(production.includes('reason: "Run Brisa as administrator'));
  assert.ok(production.includes("active: false, owned: false, reliable: false"));
});

test("production wires scoped Discord restart, route proof and post-start settling", () => {
  assert.match(production, /stopDiscord:/);
  assert.match(production, /validateDiscordApps: trustedApps/);
  assert.match(production, /createDiscordSelectionGuard\(discordExecutables\)/);
  assert.match(production, /cleanup \? selectionGuard\.assertCleanup : trustedApps/);
  assert.match(production, /discordRunning:/);
  assert.match(production, /verifyRoute:/);
  assert.match(production, /discordAllowedApps\(apps\)/);
  // Settling moved into native start so the timer overlaps ownership inspection.
  assert.doesNotMatch(production, /await delay\(2_000/);
  assert.match(source, /settled = delay\(2_000/);
  assert.match(production, /await startNativeWireSock\(/);
  assert.doesNotMatch(production, /detached: true/);
});

test("native start prepares WireSock on demand before launching the owned tunnel", () => {
  // One preparation returns the whole validated pair; a second discovery here could pick a
  // different pair than the one just validated, and repeated the registry/CIM cost.
  assert.match(source, /await ensureWireSockCandidate\(\(\) => \{\}, \{ signal, onEvent: options\?\.onEvent \}\)/);
  assert.doesNotMatch(source, /findWireSockCandidate\(\)/);
  assert.match(source, /return \{ configPath: target, candidate \}/);
});

test("WireSock setup launches the verified vendor installer visibly, never silently", () => {
  const vpnWindows = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../src/network/vpn-windows.ts"), "utf8");
  assert.match(vpnWindows, /WIRESOCK_INSTALLER_SHA256/);
  // Elevation goes through ProcessStartInfo so a declined prompt raises a Win32Exception the
  // wrapper can recognise, instead of an InvalidOperationException with no error code.
  assert.match(vpnWindows, /Diagnostics.ProcessStartInfo/);
  assert.match(vpnWindows, /\$psi\.Verb = 'runas'/);
  assert.doesNotMatch(vpnWindows, /Start-Process -FilePath/);
  assert.doesNotMatch(vpnWindows, /ArgumentList @\('\/quiet','\/norestart'\)/);
});

test("native WireSock start uses direct application mode without global service or process mutation", () => {
  assert.match(source, /['\"]run['\"]/);
  assert.doesNotMatch(source, /\binstall\s+-|Stop-Service|taskkill|execFileSync|Get-Process[^\n]*Stop-Process/);
  assert.match(source, /Stop-Process -Id \$child\.Id/);
  assert.match(source, /cancel/i);
  assert.ok(source.includes("elevatedPowerShellFileArgs(scriptPath, resultPath)"));
  assert.ok(source.includes("file === cancelPath && cancellationMarked"));
  assert.match(source, /Get-CimInstance Win32_(?:Service|Process)/);
});

test("start refusals are classified instead of collapsed into one message", () => {
  assert.match(source, /classifyStartResult\(/);
  assert.match(source, /WireSockStartError/);
  assert.match(source, /START_STATE_UNRELIABLE/);
  assert.match(source, /START_EXTERNAL_ACTIVE/);
  assert.match(source, /START_NOT_CONFIRMED/);
  // Vendor output is never read: the result file carries the only code that leaves here.
  assert.doesNotMatch(source, /readFileSync\([^)]*stderr/);
});

test("production persists diagnostics inside the private data directory", () => {
  assert.match(production, /createDiagnostics\(\{ file: path\.join\(dataDir, "diagnostics\.log"\) \}\)/);
  assert.match(production, /diag: diagnostics/);
  assert.match(production, /if \(!elevated\) diagnostics\.record\("startup", "NOT_ELEVATED"\)/);
});


// Evaluate only the production token-check block with a fake child process; no startup I/O.
function tokenCheck(output, failure = null) {
  const block = production.slice(production.indexOf("  let elevated = false;"), production.indexOf("  // Every mutation refuses"));
  const calls = [];
  const elevated = new Function("execFileSync", "powerShellCommandArgs", `${block}\nreturn elevated;`)((...args) => {
    calls.push(args);
    if (failure) throw failure;
    return output;
  }, () => []);
  return { elevated, calls };
}

test("speed K2 accepts the exact high-integrity SID and whoami arguments", () => {
  const result = tokenCheck('"Rótulo obrigatório\\Nível alto","Rótulo","S-1-16-12288",""\r\n');
  assert.equal(result.elevated, true);
  assert.deepEqual(result.calls[0].slice(0, 2), ["whoami.exe", ["/groups", "/fo", "csv", "/nh"]]);
  assert.equal(result.calls[0][2].timeout, 5000);
  assert.equal(result.calls[0][2].windowsHide, true);
});

for (const [label, output] of [
  ["medium", '"Medium","Label","S-1-16-8192",""'],
  ["admin-group-only", '"Administrators","Alias","S-1-5-32-544","Deny only"'],
  ["longer-SID", '"High","Label","S-1-16-122880",""'],
  ["prefix-SID", '"High","Label","X-S-1-16-12288",""'],
  ["name-only", '"S-1-16-12288","Label","S-1-16-8192",""'],
  ["empty", ""],
]) test(`speed K2 fails closed for ${label}`, () => assert.equal(tokenCheck(output).elevated, false));

test("speed K2 fails closed when whoami throws", () => {
  assert.equal(tokenCheck("", new Error("synthetic spawn failure")).elevated, false);
});

function deferred() {
  let resolve, reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

// Compile the real function, replacing every imported dependency with fakes.
// Never import or invoke the production backend, PowerShell, SDK, or real filesystem.
function nativeHarness({ after = deferred(), beforeActive = false, verdictOk = true, missingLease = false } = {}) {
  const events = [], timer = deferred(), wrapper = deferred();
  let timerSignal, inspections = 0, completed = false, discoveries = 0;
  const candidate = { executable: "sdk.exe", booster: "booster.dll", executableVersion: "1", boosterVersion: "1" };
  const fakeFs = {
    mkdirSync() {}, readdirSync() { return []; }, writeFileSync() {}, rmSync() {}, renameSync() {},
    readFileSync() { return "0\nDIRECT_RUNNING: pid=7\n"; },
  };
  const deps = {
    process: { platform: "win32", arch: "x64", pid: 123, env: {} }, fs: fakeFs, path: path.win32,
    execFile: (_file, _args, _options, callback) => { wrapper.promise.then(() => callback(null)); },
    elevatedPowerShellFileArgs: () => [], quotePowerShell: value => `'${value}'`,
    WireSockSetupError: class extends Error {},
    WireSockStartError: class extends Error { constructor(code) { super(code); this.code = code; } },
    classifyStartResult: () => verdictOk ? { ok: true } : { ok: false, code: "START_NOT_APPROVED" },
    ensureWireSockCandidate: async () => candidate,
    findWireSockCandidate: async () => { discoveries++; return candidate; },
    learnCandidateLease: async () => missingLease ? null : { ...candidate, executableDigest: "a".repeat(64), boosterDigest: "b".repeat(64) },
    inspectWireSockAsync: async () => {
      if (++inspections === 1) return { reliable: true, active: beforeActive, owned: beforeActive };
      events.push("inspect-after"); return after.promise;
    },
    formatAllowedApps: () => "Discord.exe", sanitizeWireGuardConfig: raw => raw,
    validateWireGuardConfig: () => ({ valid: true }),
    delay: (ms, value, { signal } = {}) => {
      events.push(["settle", ms]); timerSignal = signal;
      const reject = () => timer.reject(new Error("synthetic aborted timer"));
      if (signal?.aborted) reject(); else signal?.addEventListener("abort", reject, { once: true });
      return timer.promise;
    },
  };
  const runnable = transformSync(source.replace(/^import .*;\r?$/gm, "").replace(/^export /gm, ""), { loader: "ts", format: "esm" }).code;
  const start = new Function(...Object.keys(deps), `${runnable}\nreturn startNativeWireSock;`)(...Object.values(deps));
  const controller = new AbortController();
  const pending = start(String.raw`C:\fixture\owned.conf`, "fixture", [String.raw`C:\Discord\Discord.exe`], controller.signal);
  pending.then(() => { completed = true; }, () => { completed = true; });
  return { events, timer, wrapper, after, pending, controller, get discoveries() { return discoveries; }, get timerSignal() { return timerSignal; }, get completed() { return completed; } };
}
const turn = () => new Promise(resolve => setImmediate(resolve));
const confirmed = { reliable: true, active: true, owned: true };

test("speed N3 starts settling only after successful wrapper and overlaps inspection", async () => {
  const h = nativeHarness();
  await turn();
  assert.deepEqual(h.events, [], "no settling before the elevated result");
  h.wrapper.resolve(); await turn();
  const overlapped = JSON.stringify(h.events);
  h.after.resolve(confirmed); await turn();
  const waitedForTimer = !h.completed;
  h.timer.resolve(); await h.pending;
  assert.equal(overlapped, JSON.stringify([["settle", 2000], "inspect-after"]));
  assert.equal(waitedForTimer, true, "fast inspection cannot skip two-second settling");
});

test("speed N3 expired settle still waits for ownership inspection", async () => {
  const h = nativeHarness(); h.wrapper.resolve(); await turn();
  h.timer.resolve(); await turn();
  assert.equal(h.completed, false);
  h.after.resolve(confirmed); await h.pending;
});

for (const after of [{ reliable: false }, { reliable: true, active: false, owned: false }, { reliable: true, active: true, owned: false }]) {
  test(`speed N3 rejects unconfirmed ownership ${JSON.stringify(after)}`, async () => {
    const h = nativeHarness();
    const checked = assert.rejects(h.pending, error => error.code === "START_NOT_CONFIRMED");
    h.wrapper.resolve(); await turn(); h.after.resolve(after); await checked;
    assert.equal(h.timerSignal?.aborted, true, "failed confirmation cancels the unused timer");
  });
}

test("speed N3 cancels timer when inspection throws", async () => {
  const h = nativeHarness(); const failure = new Error("synthetic inspection failure");
  const checked = assert.rejects(h.pending, error => error === failure);
  h.wrapper.resolve(); await turn(); h.after.reject(failure); await checked;
  assert.equal(h.timerSignal?.aborted, true);
});

for (const duringInspection of [false, true]) {
  test(`speed N3 aborts cleanly with inspection pending=${duringInspection}`, async () => {
    const h = nativeHarness(); const checked = assert.rejects(h.pending); checked.catch(() => {});
    h.wrapper.resolve(); await turn();
    if (!duringInspection) { h.after.resolve(confirmed); await turn(); }
    h.controller.abort(); await turn();
    h.after.resolve(confirmed); await checked;
    assert.equal(h.timerSignal?.aborted, true);
  });
}

test("speed N3 rejected wrapper never begins settling", async () => {
  const h = nativeHarness({ verdictOk: false });
  const checked = assert.rejects(h.pending, error => error.code === "START_NOT_APPROVED");
  h.wrapper.resolve(); await checked;
  assert.deepEqual(h.events, []);
});

test("speed N3 already-owned shortcut retains production settling", async () => {
  const h = nativeHarness({ beforeActive: true }); await turn();
  const waited = !h.completed;
  h.timer.resolve(); await h.pending;
  assert.equal(waited, true);
  assert.deepEqual(h.events, [["settle", 2000]]);
});


test("speed N2 native start refuses a missing lease before elevated launch", async () => {
  const h = nativeHarness({ missingLease: true });
  const checked = assert.rejects(h.pending, error => error.code === "START_NOT_APPROVED"); checked.catch(() => {});
  h.wrapper.resolve(); h.after.resolve(confirmed); h.timer.resolve();
  await checked;
  assert.deepEqual(h.events, []);
  assert.doesNotMatch(source, /await findWireSockCandidate\(/);
});

test("speed N2 native start uses the learned lease without another full probe", async () => {
  const h = nativeHarness();
  h.wrapper.resolve(); h.after.resolve(confirmed); h.timer.resolve();
  await h.pending;
  assert.equal(h.discoveries, 0);
});


// Run ONLY the service refusal guard, ending before process inspection and launch.
const directText = source.slice(source.indexOf("export function nativeDirectScript("), source.indexOf("type ExecFailure"));
const directScript = new Function("quotePowerShell", `${transformSync(directText.replace("export ", ""), { loader: "ts", format: "esm" }).code}\nreturn nativeDirectScript;`)(quotePowerShell)("fixture.exe", "fixture.conf", "fixture.result", "fixture.cancel");
const guardStart = directScript.indexOf("  Assert-NotCancelled\n") + "  Assert-NotCancelled\n".length;
const serviceGuard = directScript.slice(guardStart, directScript.indexOf("  $processes=@("));
function guardFixture(fixture) {
  assert.ok(guardStart > 0 && serviceGuard.length > 0);
  const script = `function Get-CimInstance { [CmdletBinding()] param($ClassName,$Filter) throw 'FORBIDDEN_CIM_SERVICE' }; ${fixture}; try { ${serviceGuard}; 'ALLOWED' } catch { $_.Exception.Message }`;
  return execFileSync("powershell.exe", powerShellCommandArgs(script), { encoding: "utf8", windowsHide: true, timeout: 10000 }).trim();
}

test("speed S1 native guard allows only canonical missing services", { skip: process.platform !== "win32" }, () => {
  const fixture = String.raw`try { Microsoft.PowerShell.Management\Get-Service -Name 'wiresock-nonexistent-test' -ErrorAction Stop } catch { $script:missingException=$_.Exception }; function Get-Service { [CmdletBinding()] param($Name) $record=New-Object Management.Automation.ErrorRecord($script:missingException,'NoServiceFoundForGivenName',[Management.Automation.ErrorCategory]::ObjectNotFound,$Name); $PSCmdlet.ThrowTerminatingError($record) }`;
  assert.equal(guardFixture(fixture), "ALLOWED");
});

test("speed S1 native guard allows stopped services without CIM", { skip: process.platform !== "win32" }, () => {
  assert.equal(guardFixture("function Get-Service { [CmdletBinding()] param($Name) [pscustomobject]@{Status='Stopped'} }"), "ALLOWED");
});

for (const status of ["Running", "StartPending", "StopPending", "Paused", "PausePending", "ContinuePending"]) {
  test(`speed S1 native guard refuses ${status}`, { skip: process.platform !== "win32" }, () => {
    assert.equal(guardFixture(`function Get-Service { [CmdletBinding()] param($Name) [pscustomobject]@{Status='${status}'} }`), "EXTERNAL_SERVICE_ACTIVE");
  });
}

for (const failure of ["Write-Error 'FIXTURE_SERVICE_FAILURE'", "throw (New-Object ComponentModel.Win32Exception(5))", "$null"]) {
  test(`speed S1 native guard refuses unknown service state: ${failure}`, { skip: process.platform !== "win32" }, () => {
    assert.equal(guardFixture(`function Get-Service { [CmdletBinding()] param($Name) ${failure} }`), "EXTERNAL_SERVICE_QUERY_FAILED");
  });
}

test("speed S1 native guard contains no service CIM or sc parser", () => {
  assert.doesNotMatch(serviceGuard, /Win32_Service|sc[.]exe|STATE\s*:/);
  assert.match(directScript, /Get-CimInstance Win32_Process -Filter "Name='wiresock-client.exe'" -ErrorAction Stop/);
});
