import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { WIRESOCK_SIGNER_SUBJECT, wireSockTrustHelpers } from "../src/wiresock-trust.mjs";
import { probeWireSock } from "../src/wiresock-probe.mjs";

const genuine = path.win32.join(process.env.ProgramFiles ?? "C:\\Program Files", "WireSock Secure Connect", "sdk");
const genuineExe = path.win32.join(genuine, "wiresock-client.exe");
const genuineBooster = path.win32.join(genuine, "wgbooster.dll");
const installed = fs.existsSync(genuineExe) && fs.existsSync(genuineBooster);

// The production verdict comes from the probe script, so the test composes the same helpers into
// a one-pair script. That keeps the security properties testable without a production export
// that only tests would call.
let lastError = "";
function verdictFor(executable, booster) {
  const script = `$ErrorActionPreference='Stop'
${wireSockTrustHelpers()}
foreach ($p in @('${executable.replace(/'/g, "''")}', '${booster.replace(/'/g, "''")}')) {
  if (-not (Test-BrisaSignature $p)) { 'UNTRUSTED'; exit }
  if (-not (Test-BrisaAcl $p)) { 'UNTRUSTED'; exit }
}
if (-not (Test-BrisaAclChain '${executable.replace(/'/g, "''")}')) { 'UNTRUSTED'; exit }
'TRUSTED'`;
  try {
    return execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
      encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], windowsHide: true, timeout: 15000,
    }).trim();
  } catch (error) {
    // A refusal is also an error: production treats an unparsable probe as untrusted, so the test
    // asserts that the verdict is never TRUSTED and carries the reason when it is an error.
    lastError = String(error?.stderr ?? error?.message ?? "").trim();
    return "ERROR";
  }
}

test("the trust helpers pin the vendor signer and inspect the parent chain", () => {
  const script = wireSockTrustHelpers();
  assert.match(script, new RegExp(WIRESOCK_SIGNER_SUBJECT.cn));
  assert.match(script, /ReparsePoint/);
  assert.match(script, /acl.Owner/);
  assert.match(script, /Get-AuthenticodeSignature/);
  assert.match(script, /Get-Acl/);
  // Generic rights have no bit in common with the specific ones, and an inherit-only ACE does
  // not apply to the object itself (the stock CREATOR OWNER ACE is GENERIC_ALL).
  assert.match(script, /0x10000000/);
  assert.match(script, /InheritOnly/);
  // The volume root is skipped: authenticated users may create folders there, which cannot
  // replace a binary inside Program Files.
  assert.match(script, /GetPathRoot/);
});

test("a signed pair in a user-writable directory is not trusted", t => {
  if (!installed) return t.skip("WireSock is not installed on this machine");
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "brisa-trust-"));
  try {
    const executable = path.join(directory, "wiresock-client.exe");
    const booster = path.join(directory, "wgbooster.dll");
    fs.copyFileSync(genuineExe, executable);
    fs.copyFileSync(genuineBooster, booster);
    // The copy keeps a valid signature, so only the ACL walk can reject it.
    assert.equal(verdictFor(executable, booster), "UNTRUSTED");
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("an unsigned file is not trusted", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "brisa-trust-"));
  try {
    const executable = path.join(directory, "wiresock-client.exe");
    fs.writeFileSync(executable, "MZ");
    // A truncated PE header is rejected as UNTRUSTED on some Windows builds and makes
    // Get-AuthenticodeSignature throw on others. Both are refusals: the probe reports an
    // unparsable result as untrusted, so only TRUSTED would be a defect here.
    const verdict = verdictFor(executable, path.join(directory, "wgbooster.dll"));
    assert.notEqual(verdict, "TRUSTED", `an unsigned file was trusted: ${lastError}`);
    assert.ok(["UNTRUSTED", "ERROR"].includes(verdict), `unexpected verdict ${verdict}`);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("the genuine vendor install is trusted", t => {
  if (!installed) return t.skip("WireSock is not installed on this machine");
  assert.equal(verdictFor(genuineExe, genuineBooster), "TRUSTED");
});

test("the probe reports the genuine install as trusted", async t => {
  if (!installed) return t.skip("WireSock is not installed on this machine");
  const result = await probeWireSock();
  assert.equal(result.parsed, true);
  const match = result.candidates.find(candidate => candidate.executable.toLowerCase() === genuineExe.toLowerCase());
  assert.ok(match, "the probe should find the installed pair");
  assert.equal(match.trusted, true);
});
