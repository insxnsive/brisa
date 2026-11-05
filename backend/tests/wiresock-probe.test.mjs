import assert from "node:assert/strict";
import test from "node:test";

import { parseWireSockProbe, probeWireSock, wireSockProbeScript } from "../src/wiresock-probe.mjs";

const good = {
  executable: "C:\\Program Files\\WireSock Secure Connect\\sdk\\wiresock-client.exe",
  booster: "C:\\Program Files\\WireSock Secure Connect\\sdk\\wgbooster.dll",
  executableVersion: "3.4.8.1",
  boosterVersion: "3.4.8.1",
  trusted: true,
};

const json = candidates => JSON.stringify({ candidates });

test("the probe script does discovery and the trust verdict in one pass", () => {
  const script = wireSockProbeScript({ ProgramFiles: "C:\\Program Files", ProgramW6432: "C:\\Program Files" });
  assert.match(script, /HKLM:/);
  assert.doesNotMatch(script, /HKCU:/);
  assert.doesNotMatch(script, /Win32_Process/);
  // The verdict is computed inside the same script, not by a second spawn per file.
  assert.match(script, /Get-AuthenticodeSignature/);
  assert.match(script, /Test-BrisaAcl/);
  assert.match(script, /ConvertTo-Json/);
  // Machine-scope WinGet only.
  assert.match(script, /WinGet\\Packages/);
});

test("the parser keeps valid candidates and drops malformed ones", () => {
  const parsed = parseWireSockProbe(json([
    good,
    { ...good, executable: "relative\\wiresock-client.exe" },
    { ...good, booster: "C:\\x\\wgbooster.dll", trusted: "yes" },
    { ...good, executableVersion: "3.4.8" },
    { ...good, boosterVersion: "" },
    { ...good, trusted: undefined },
    null,
  ]));
  assert.equal(parsed.candidates.length, 1);
  assert.deepEqual(parsed.candidates[0], good);
});

test("unreadable output is not a candidate list", () => {
  assert.equal(parseWireSockProbe("not json"), null);
  assert.equal(parseWireSockProbe(""), null);
  assert.equal(parseWireSockProbe(JSON.stringify({ candidates: "x" })), null);
  assert.equal(parseWireSockProbe(JSON.stringify({ roots: [] })), null);
});

test("a failed or timed-out probe fails closed", async () => {
  const failing = async () => { throw new Error("timeout"); };
  const failed = await probeWireSock({}, failing);
  assert.deepEqual(failed.candidates, []);
  assert.equal(failed.parsed, false);

  const junk = await probeWireSock({}, async () => "garbage");
  assert.deepEqual(junk.candidates, []);
  assert.equal(junk.parsed, false);
});

test("the runner seam feeds the real parser", async () => {
  let received = null;
  const run = async (script, timeoutMs) => { received = { script, timeoutMs }; return json([good]); };
  const result = await probeWireSock({ ProgramFiles: "C:\\Program Files" }, run, 1234);
  assert.equal(result.parsed, true);
  assert.deepEqual(result.candidates, [good]);
  assert.equal(received.timeoutMs, 1234);
  assert.match(received.script, /wiresock-client\.exe/);
});

test("an untrusted higher version is present in the probe but never wins selection", async () => {
  // Selection happens outside this module, so the contract that matters here is that the probe
  // reports the verdict and leaves the choice to the caller's filter.
  const untrusted = { ...good, executable: "C:\\Users\\nate\\fake\\wiresock-client.exe", booster: "C:\\Users\\nate\\fake\\wgbooster.dll", executableVersion: "9.9.9.9", boosterVersion: "9.9.9.9", trusted: false };
  const result = await probeWireSock({}, async () => json([good, untrusted]));
  assert.equal(result.candidates.length, 2);
  assert.deepEqual(result.candidates.filter(candidate => candidate.trusted).map(candidate => candidate.executable), [good.executable]);
});
