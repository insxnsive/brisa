import { build } from "esbuild";
import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

// Prelaunch cancellation for the WireSock SDK setup. No network, no installer and no UAC:
// every case here is proven by the classification the caller receives, which is what decides
// whether the app says "cancelled" or "WireSock must be installed".
const here = path.dirname(fileURLToPath(import.meta.url));
const bundled = await build({
  entryPoints: [path.join(here, "..", "src", "network", "vpn-windows.ts")],
  bundle: true, format: "esm", platform: "node", write: false, logLevel: "silent",
});
const { ensureWireSockInstalled, ensureWireSockCandidate, runElevatedInstaller, downloadInstaller, learnCandidateLease } = await import(
  `data:text/javascript;base64,${Buffer.from(bundled.outputFiles[0].text).toString("base64")}`
);
const { WireSockSetupError } = await import("../src/wiresock-setup.mjs");

const quietLog = () => {};

test("a pre-aborted setup is reported as a cancellation, not as a missing SDK", async () => {
  const controller = new AbortController();
  controller.abort();
  let caught = null;
  try {
    await ensureWireSockInstalled(quietLog, { signal: controller.signal });
  } catch (error) {
    caught = error;
  }
  assert.ok(caught, "the setup must reject");
  assert.equal(caught.name, "AbortError");
  assert.equal(caught instanceof WireSockSetupError, false, "a cancel must not become WIRESOCK_SETUP_REQUIRED");
});

test("an aborted elevation is refused before any process is created", async () => {
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(runElevatedInstaller(String.raw`C:\nonexistent\wiresock-sdk.exe`, controller.signal), error => {
    assert.equal(error.name, "AbortError");
    return true;
  });
});

test("a request error after the file opens waits for the descriptor to close", async () => {
  // HTTP 200 already created the output stream, so a request-level failure must join the same
  // close-wait as every other transfer failure. The proof is that the file can be deleted the
  // moment the promise rejects: on Windows an open descriptor makes that fail.
  const target = path.join(os.tmpdir(), `brisa-download-fixture-${process.pid}.exe`);
  const request = new EventEmitter();
  request.setTimeout = () => {};
  request.destroy = () => {};
  const response = new EventEmitter();
  response.statusCode = 200;
  response.headers = {};
  response.resume = () => {};
  response.destroy = () => {};
  response.pipe = destination => { response.on("data", chunk => destination.write(chunk)); };
  const fakeHttp = { get: (url, options, callback) => { setImmediate(() => callback(response)); return request; } };

  const pending = downloadInstaller(target, "https://wiresock.net/WireSock-3.4.8.1.exe", 0, undefined, fakeHttp);
  await new Promise(resolve => setImmediate(resolve));
  response.emit("data", Buffer.from("synthetic payload"));
  request.emit("error", new Error("synthetic socket error"));

  await assert.rejects(pending, /synthetic socket error/);
  await fs.promises.rm(target, { force: true });
});

test("a cancellation after launch waits for the installer to exit instead of killing it", async () => {
  // Deferred cancellation: killing the PowerShell wrapper would not stop the elevated installer,
  // so the setup must wait for the real exit and then report the cancellation.
  const controller = new AbortController();
  const child = new EventEmitter();
  let killed = false;
  child.kill = () => { killed = true; };
  setImmediate(() => child.emit("spawn"));
  let finish = null;
  let pendingSeen = false;
  const completionPath = path.join(os.tmpdir(), `brisa-complete-wait-${process.pid}.txt`);
  const exec = (file, args, options, callback) => { finish = () => { fs.writeFileSync(completionPath, "COMPLETED:0"); callback(null); }; return child; };

  const pending = runElevatedInstaller(String.raw`C:\temp\wiresock-sdk.exe`, controller.signal, {
    exec,
    completionPath,
    onPending: () => { pendingSeen = true; },
  });
  await new Promise(resolve => setImmediate(resolve));
  controller.abort();

  let settled = false;
  void pending.then(() => { settled = true; }, () => { settled = true; });
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(settled, false, "the setup must stay pending while the visible installer runs");
  assert.equal(pendingSeen, true, "the pending cancellation must be reported");

  finish(null);
  await assert.rejects(pending, error => {
    assert.equal(error.name, "AbortError");
    return true;
  });
  assert.equal(killed, false, "the elevated installer must never be force-killed");
});

test("a lease is refused when discovery no longer confirms the same pair or versions", async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "brisa-lease-fixture-"));
  const executable = path.join(directory, "wiresock-client.exe");
  const booster = path.join(directory, "wgbooster.dll");
  fs.writeFileSync(executable, "exe-bytes");
  fs.writeFileSync(booster, "dll-bytes");
  const candidate = { executable, booster, executableVersion: "3.4.8.1", boosterVersion: "3.4.8.1" };
  assert.equal(await learnCandidateLease(candidate, undefined, () => ({ ...candidate, executableVersion: "3.4.9.0" })), null, "a changed version label must invalidate the lease");
  assert.equal(await learnCandidateLease(candidate, undefined, () => null), null, "a vanished candidate must invalidate the lease");
  const learned = await learnCandidateLease(candidate, undefined, () => candidate);
  assert.ok(learned && learned.executableDigest.length === 64, "a stable window yields a lease");
});

test("a spawn failure is not reported as a vendor exit", async () => {
  // ENOENT on powershell.exe means no vendor process ever ran, so EXITED would be a fiction.
  const events = [];
  const child = new EventEmitter();
  const exec = () => {
    setImmediate(() => child.emit("error", Object.assign(new Error("spawn powershell.exe ENOENT"), { code: "ENOENT" })));
    return child;
  };
  await assert.rejects(
    runElevatedInstaller(String.raw`C:\temp\wiresock-sdk.exe`, undefined, { exec, onExited: info => events.push(info) }),
    /ENOENT/
  );
  assert.deepEqual(events, [], "a spawn failure must not report a vendor exit");
});

test("a pending cancellation still reports the vendor exit before the setup cancel", async () => {
  // Event order for the D3 policy: the visible installer runs to completion, so the exit is
  // reported even though the setup itself ends as a cancellation.
  const controller = new AbortController();
  const events = [];
  const child = new EventEmitter();
  child.kill = () => {};
  setImmediate(() => child.emit("spawn"));
  const completionPath = path.join(os.tmpdir(), `brisa-complete-order-${process.pid}.txt`);
  let finish = null;
  const pending = ensureWireSockCandidate(quietLog, {
    signal: controller.signal,
    findCandidate: () => null,
    mkdtemp: async () => String.raw`C:\temp\brisa-wiresock-fixture`,
    download: async () => {},
    verify: async () => true,
    remove: async () => {},
    completionPath,
    exec: (file, args, options, callback) => { finish = () => { fs.writeFileSync(completionPath, "COMPLETED:0"); callback(null); }; return child; },
    onEvent: (code, data) => events.push([code, data]),
  });
  await new Promise(resolve => setImmediate(resolve));
  controller.abort();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(events.map(entry => entry[0]), [
    "SDK_CHECK_STARTED",
    "SDK_DOWNLOAD_STARTED",
    "SDK_DOWNLOAD_VERIFIED",
    "SDK_INSTALL_STARTED",
    "SDK_INSTALL_CANCEL_PENDING",
  ]);
  finish(null);
  await assert.rejects(pending, error => { assert.equal(error.name, "AbortError"); return true; });
  assert.deepEqual(events.map(entry => entry[0]), [
    "SDK_CHECK_STARTED",
    "SDK_DOWNLOAD_STARTED",
    "SDK_DOWNLOAD_VERIFIED",
    "SDK_INSTALL_STARTED",
    "SDK_INSTALL_CANCEL_PENDING",
    "SDK_INSTALL_EXITED",
    "SDK_SETUP_CANCELLED",
  ]);
  assert.equal(events[5][1].cancelled, true, "the exit must record that a cancel was pending");
});

test("a temp-directory failure during a cancellation is still a cancellation", async () => {
  const controller = new AbortController();
  await assert.rejects(
    ensureWireSockCandidate(quietLog, {
      signal: controller.signal,
      findCandidate: () => null,
      mkdtemp: async () => { controller.abort(); throw new Error("synthetic mkdtemp failure"); },
    }),
    error => {
      assert.equal(error.name, "AbortError");
      assert.equal(error instanceof WireSockSetupError, false);
      return true;
    }
  );
});

test("an aborted download releases its temp directory and reports cancellation", async () => {
  const controller = new AbortController();
  const removed = [];
  await assert.rejects(
    ensureWireSockCandidate(quietLog, {
      signal: controller.signal,
      findCandidate: () => null,
      mkdtemp: async () => String.raw`C:\temp\brisa-wiresock-fixture`,
      download: async () => { controller.abort(); const error = new Error("aborted"); error.name = "AbortError"; throw error; },
      remove: async directory => { removed.push(directory); },
    }),
    error => { assert.equal(error.name, "AbortError"); return true; }
  );
  assert.deepEqual(removed, [String.raw`C:\temp\brisa-wiresock-fixture`], "the temp directory must be removed exactly once");
});

test("a cancellation during cleanup cannot return a setup failure", async () => {
  const controller = new AbortController();
  await assert.rejects(
    ensureWireSockCandidate(quietLog, {
      signal: controller.signal,
      findCandidate: () => null,
      mkdtemp: async () => String.raw`C:\temp\brisa-wiresock-fixture`,
      download: async () => { throw new Error("synthetic download failure"); },
      // A limpeza é onde o cancelamento chega: o resultado não pode ser WireSockSetupError.
      remove: async () => { controller.abort(); },
    }),
    error => {
      assert.equal(error.name, "AbortError");
      assert.equal(error instanceof WireSockSetupError, false);
      return true;
    }
  );
});

test("a nonzero vendor exit is a confirmed result and still reports the exit", async () => {
  const events = [];
  const child = new EventEmitter();
  setImmediate(() => child.emit("spawn"));
  const completionPath = path.join(os.tmpdir(), `brisa-complete-${process.pid}-${Math.random()}.txt`);
  const exec = (file, args, options, callback) => {
    setImmediate(() => { fs.writeFileSync(completionPath, "COMPLETED:1"); callback(Object.assign(new Error("installer exited 1"), { code: 1 })); });
    return child;
  };
  await assert.rejects(runElevatedInstaller(String.raw`C:	empwiresock-sdk.exe`, undefined, { exec, completionPath, onExited: info => events.push(info) }), /exited with code 1/);
  assert.equal(events.length, 1, "a confirmed vendor exit must be reported once");
  assert.equal(events[0].cancelled, false);
});

test("a numeric wrapper failure without a completion record is not a vendor result", async () => {
  // The wrapper catch exits 1 and UAC decline exits 1223, both without a record: a numeric code
  // alone must never be read as the installer finishing.
  const events = [];
  const child = new EventEmitter();
  setImmediate(() => child.emit("spawn"));
  const exec = (file, args, options, callback) => {
    setImmediate(() => callback(Object.assign(new Error("wrapper failed"), { code: 1 })));
    return child;
  };
  await assert.rejects(
    runElevatedInstaller(String.raw`C:	empwiresock-sdk.exe`, undefined, { exec, onExited: info => events.push(info) }),
    error => { assert.equal(error.name, "InstallerSupervisionLost"); return true; }
  );
  assert.deepEqual(events, [], "a wrapper failure must not report a vendor exit");
});
test("a malformed completion record keeps the installer unknown", async () => {
  // Only exact REFUSED or COMPLETED:<integer> prove anything. A bare value or junk on disk must
  // not be read as the vendor finishing.
  const completionPath = path.join(os.tmpdir(), `brisa-complete-junk-${process.pid}.txt`);
  for (const junk of ["0", "COMPLETED:", "completed:0", "COMPLETED:0extra"]) {
    const child = new EventEmitter();
    setImmediate(() => child.emit("spawn"));
    const events = [];
    const pending = runElevatedInstaller(String.raw`C:	empwiresock-sdk.exe`, undefined, {
      completionPath,
      onExited: info => events.push(info),
      exec: (file, args, options, callback) => {
        setImmediate(() => { fs.writeFileSync(completionPath, junk); callback(null); });
        return child;
      },
    });
    await assert.rejects(pending, error => {
      assert.equal(error.name, "InstallerSupervisionLost", `junk record ${JSON.stringify(junk)} must not confirm completion`);
      return true;
    });
    assert.deepEqual(events, [], "no exit may be reported for a malformed record");
  }
});
test("a refused elevation is a normal setup decline, not a lost installer", async () => {
  // UAC decline exits 1223 with a REFUSED record: it must keep the existing declined guidance,
  // clean up, and leave the setup slot free for a later attempt.
  const completionPath = path.join(os.tmpdir(), `brisa-complete-refused-${process.pid}.txt`);
  const child = new EventEmitter();
  setImmediate(() => child.emit("spawn"));
  let removed = 0;
  const pending = ensureWireSockCandidate(quietLog, {
    findCandidate: () => null,
    mkdtemp: async () => String.raw`C:	emprisa-wiresock-refused`,
    download: async () => {},
    verify: async () => true,
    remove: async () => { removed += 1; },
    completionPath,
    exec: (file, args, options, callback) => {
      setImmediate(() => { fs.writeFileSync(completionPath, "REFUSED"); callback(Object.assign(new Error("elevation refused"), { code: 1223 })); });
      return child;
    },
  });
  await assert.rejects(pending, error => {
    // The bundle carries its own copy of the class, so assert the observable shape.
    assert.equal(error.cancelled, true, "a refusal keeps the declined guidance");
    return true;
  });
  assert.equal(removed, 1, "a refusal cleans up its temp directory");
  // The slot is free: a later attempt reaches its own abort check instead of being refused.
  await assert.rejects(
    ensureWireSockCandidate(quietLog, { findCandidate: () => null, signal: AbortSignal.abort() }),
    error => error.name === "AbortError"
  );
});
test("candidates that exist but are untrusted are reported, and setup fails", async () => {
  const events = [];
  const probeRun = async () => JSON.stringify({
    candidates: [{
      executable: "C:\\Users\\nate\\fake\\wiresock-client.exe",
      booster: "C:\\Users\\nate\\fake\\wgbooster.dll",
      executableVersion: "9.9.9.9",
      boosterVersion: "9.9.9.9",
      trusted: false,
    }],
  });
  await assert.rejects(
    ensureWireSockCandidate(quietLog, {
      probeRun,
      // The untrusted candidate is rejected, so setup must fall through to the install path,
      // which fails here before touching the network or elevation.
      mkdtemp: async () => { throw new Error("no temporary directory in this test"); },
      onEvent: (code, data) => events.push([code, data]),
    }),
    error => error?.cancelled === false && /WireSock Secure Connect SDK/.test(String(error.message)),
  );
  assert.deepEqual(events.map(([code]) => code).slice(0, 2), ["SDK_CHECK_STARTED", "SDK_CANDIDATE_UNTRUSTED"]);
  assert.equal(events[1][1].count, 1);
  assert.ok(events.some(([code]) => code === "SDK_SETUP_FAILED"));
});

test("a supervision failure after launch keeps the slot and reports no exit", async () => {
  // Spawn, then a pending cancel, then the wrapper dies without a vendor result: nothing here
  // proves the installer finished, so no EXITED, no cancellation and no cleanup may happen.
  const controller = new AbortController();
  const events = [];
  const child = new EventEmitter();
  child.kill = () => {};
  setImmediate(() => child.emit("spawn"));
  let removed = 0;
  const pending = ensureWireSockCandidate(quietLog, {
    signal: controller.signal,
    findCandidate: () => null,
    mkdtemp: async () => String.raw`C:	emprisa-wiresock-fixture`,
    download: async () => {},
    verify: async () => true,
    remove: async () => { removed += 1; },
    exec: () => child,
    onEvent: (code, data) => events.push([code, data]),
  });
  await new Promise(resolve => setImmediate(resolve));
  controller.abort();
  await new Promise(resolve => setImmediate(resolve));
  child.emit("error", Object.assign(new Error("wrapper died"), { killed: true }));
  await assert.rejects(pending, error => {
    assert.equal(error.name, "InstallerSupervisionLost");
    return true;
  });
  assert.ok(!events.some(entry => entry[0] === "SDK_INSTALL_EXITED"), "no exit may be reported");
  assert.ok(!events.some(entry => entry[0] === "SDK_SETUP_CANCELLED"), "no completed cancellation may be reported");
  assert.ok(events.some(entry => entry[0] === "SDK_INSTALL_SUPERVISION_LOST"));
  assert.equal(removed, 0, "the temp directory must be kept while the vendor state is unknown");
  // The slot stays held: a retry refuses instead of starting a second installation.
  await assert.rejects(ensureWireSockCandidate(quietLog, { findCandidate: () => null }), error => error.name === "InstallerSupervisionLost");
});
