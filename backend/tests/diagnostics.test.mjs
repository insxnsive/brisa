import assert from "node:assert/strict";
import test from "node:test";

import { createDiagnostics } from "../src/diagnostics.mjs";

const memoryFs = (initial = {}) => {
  const files = new Map(Object.entries(initial));
  return {
    files,
    statSync: path => {
      if (!files.has(path)) throw new Error("missing");
      return { size: Buffer.byteLength(files.get(path)) };
    },
    readFileSync: path => files.get(path) ?? "",
    writeFileSync: (path, value) => files.set(path, value),
    appendFileSync: (path, value) => files.set(path, (files.get(path) ?? "") + value),
  };
};

const fixedNow = (start = Date.parse("2026-01-01T00:00:00.000Z")) => {
  let value = start;
  return () => (value += 1_000);
};

test("events are recorded in order and formatted with codes and numbers only", () => {
  const diagnostics = createDiagnostics({ now: fixedNow() });
  diagnostics.record("starting-tunnel", "START_CLIENT_EXITED", { exitCode: 1, timedOut: false });
  diagnostics.record("stop", "STOP_NOT_CONFIRMED", { services: 2 });
  assert.deepEqual(diagnostics.recent(), [
    "2026-01-01T00:00:01.000Z starting-tunnel START_CLIENT_EXITED exitCode=1 timedOut=false",
    "2026-01-01T00:00:02.000Z stop STOP_NOT_CONFIRMED services=2",
  ]);
});

test("the ring keeps the newest events and recent() can be limited", () => {
  const diagnostics = createDiagnostics({ now: fixedNow(), maxEvents: 3 });
  for (let index = 0; index < 6; index += 1) diagnostics.record("startup", `CODE_${index}`);
  assert.equal(diagnostics.recent().length, 3);
  assert.match(diagnostics.recent()[0], /CODE_3/);
  assert.equal(diagnostics.recent(1).length, 1);
});

test("invalid stages, codes and data values are dropped instead of recorded", () => {
  const diagnostics = createDiagnostics({ now: fixedNow() });
  diagnostics.record("Starting Tunnel", "START_FAILED");
  diagnostics.record("startup", "lowercase");
  diagnostics.record("startup", "OK_CODE", {
    path: "C:\\Users\\someone\\AppData",
    freeText: "the tunnel failed because of something",
    nested: { a: 1 },
    exitCode: 1,
    // Forma não é procedência: um texto que só parece código, como um usuário em maiúsculas,
    // é descartado; só valores que o app define entram.
    allowed: "START_REFUSED",
    username: "NATE",
    trust: "TRUSTED",
  });
  const [line] = diagnostics.recent();
  assert.match(line, /OK_CODE/);
  assert.doesNotMatch(line, /START_REFUSED/, "a shape-only code that the app does not define is dropped");
  assert.doesNotMatch(line, /NATE/, "an uppercase username is not mistaken for a code");
  assert.match(line, /trust=TRUSTED/, "an app-owned code value is kept");
  assert.match(line, /exitCode=1/);
  assert.doesNotMatch(line, /someone|AppData|tunnel failed|nested/);
  assert.equal(diagnostics.recent().length, 1, "the two invalid events were dropped");
});

test("record never throws, whatever it is given", () => {
  const diagnostics = createDiagnostics({ now: fixedNow() });
  const circular = { exitCode: 1 };
  circular.self = circular;
  for (const call of [
    () => diagnostics.record(),
    () => diagnostics.record(undefined, undefined, undefined),
    () => diagnostics.record("startup", "OK_CODE", circular),
    () => diagnostics.record("startup", "OK_CODE", "not an object"),
    () => diagnostics.record("startup", "OK_CODE", { exitCode: Number.NaN, other: Infinity }),
  ]) assert.doesNotThrow(call);
  assert.equal(diagnostics.recent().length, 3, "valid stage and code still record, with empty data");
});

test("events are appended to the file when one is configured", () => {
  const fs = memoryFs();
  const diagnostics = createDiagnostics({ now: fixedNow(), file: "C:\\data\\diagnostics.log", fs });
  diagnostics.record("startup", "NOT_ELEVATED");
  diagnostics.record("plan", "PLAN_LOOKUP_FAILED", { freeOnly: true });
  const lines = fs.files.get("C:\\data\\diagnostics.log").trim().split("\n");
  assert.equal(lines.length, 2);
  assert.match(lines[0], /NOT_ELEVATED/);
  assert.match(lines[1], /PLAN_LOOKUP_FAILED freeOnly=true/);
});

test("the file is rotated at a line boundary and stays under the cap", () => {
  const fs = memoryFs({ "C:\\data\\diagnostics.log": "x".repeat(0) });
  const diagnostics = createDiagnostics({ now: fixedNow(), file: "C:\\data\\diagnostics.log", fs, maxFileBytes: 240 });
  for (let index = 0; index < 40; index += 1) diagnostics.record("startup", `CODE_${index}`, { index });
  const written = fs.files.get("C:\\data\\diagnostics.log");
  assert.ok(Buffer.byteLength(written) <= 240, `file grew to ${Buffer.byteLength(written)} bytes`);
  assert.match(written, /^2026-/, "the file starts at a whole event, not mid-line");
  assert.match(written.trim().split("\n").at(-1), /CODE_39/);
});

test("a file that cannot be written does not break the in-memory ring", () => {
  const fs = {
    statSync: () => ({ size: 0 }),
    readFileSync: () => "",
    writeFileSync: () => { throw new Error("denied"); },
    appendFileSync: () => { throw new Error("denied"); },
  };
  const diagnostics = createDiagnostics({ now: fixedNow(), file: "C:\\data\\diagnostics.log", fs });
  assert.doesNotThrow(() => diagnostics.record("startup", "NOT_ELEVATED"));
  assert.equal(diagnostics.recent().length, 1);
});
