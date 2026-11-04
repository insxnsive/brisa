import assert from "node:assert/strict";
import test from "node:test";

import { classifyStartResult } from "../src/wiresock-start-result.mjs";

const refusal = detail => `1\r\nDIRECT_REFUSED: ${detail}`;

test("the success line is the only success", () => {
  assert.deepEqual(classifyStartResult("0\r\nDIRECT_RUNNING: pid=123"), { ok: true });
  assert.deepEqual(classifyStartResult("0\nDIRECT_RUNNING: pid=123\n"), { ok: true });
  // Near misses must not be read as success.
  for (const text of ["0\nDIRECT_RUNNING: pid=abc", "1\nDIRECT_RUNNING: pid=123", "DIRECT_RUNNING: pid=123", "0\nDIRECT_RUNNING: pid="]) {
    assert.equal(classifyStartResult(text).ok, false, text);
  }
});

test("each refusal reason maps to its own code", () => {
  assert.deepEqual(classifyStartResult(refusal("EXTERNAL_SERVICE_ACTIVE")),
    { ok: false, code: "START_EXTERNAL_SERVICE", data: {} });
  assert.deepEqual(classifyStartResult(refusal("EXTERNAL_PROCESS_ACTIVE")),
    { ok: false, code: "START_EXTERNAL_PROCESS", data: {} });
  assert.deepEqual(classifyStartResult(refusal("CANCELLED")),
    { ok: false, code: "START_CANCELLED", data: {} });
  assert.deepEqual(classifyStartResult(refusal("CANCELLED_AFTER_START")),
    { ok: false, code: "START_CANCELLED", data: {} });
});

test("a client that exited keeps only its exit code", () => {
  assert.deepEqual(classifyStartResult(refusal("DIRECT_EXITED: 1")),
    { ok: false, code: "START_CLIENT_EXITED", data: { exitCode: 1 } });
  assert.deepEqual(classifyStartResult(refusal("DIRECT_EXITED: -1073741819")),
    { ok: false, code: "START_CLIENT_EXITED", data: { exitCode: -1073741819 } });
});

test("an empty result means the administrator step never completed", () => {
  assert.deepEqual(classifyStartResult("", { exitCode: 1223, timedOut: false }),
    { ok: false, code: "START_NOT_APPROVED", data: { exitCode: 1223, timedOut: false } });
  assert.deepEqual(classifyStartResult("", null), { ok: false, code: "START_NOT_APPROVED", data: {} });
  assert.deepEqual(classifyStartResult("   \r\n", { timedOut: true }),
    { ok: false, code: "START_NOT_APPROVED", data: { timedOut: true } });
});

test("vendor prose, paths and unknown details never leave the module", () => {
  const hostile = refusal("C:\\Users\\someone\\secret path failed: access denied");
  const verdict = classifyStartResult(hostile);
  assert.deepEqual(verdict, { ok: false, code: "START_REFUSED_UNKNOWN", data: {} });
  assert.doesNotMatch(JSON.stringify(verdict), /secret|someone/);
  assert.equal(classifyStartResult("garbage without a code").code, "START_REFUSED_UNKNOWN");
  assert.equal(classifyStartResult(null).code, "START_NOT_APPROVED");
});
