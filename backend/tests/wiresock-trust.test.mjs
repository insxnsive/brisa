import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import { WIRESOCK_SIGNER_THUMBPRINT, candidateIsTrusted, wireSockTrustScript } from "../src/wiresock-trust.mjs";

const genuine = path.win32.join(process.env.ProgramFiles ?? "C:\\Program Files", "WireSock Secure Connect", "sdk");
const genuineExe = path.win32.join(genuine, "wiresock-client.exe");
const genuineBooster = path.win32.join(genuine, "wgbooster.dll");
const installed = fs.existsSync(genuineExe) && fs.existsSync(genuineBooster);

test("the trust check pins the vendor signer and inspects the parent chain", () => {
  const script = wireSockTrustScript("C:\\Program Files\\WireSock Secure Connect\\sdk\\wiresock-client.exe", "C:\\Program Files\\WireSock Secure Connect\\sdk\\wgbooster.dll");
  assert.match(script, new RegExp(WIRESOCK_SIGNER_THUMBPRINT));
  assert.match(script, /Get-AuthenticodeSignature/);
  assert.match(script, /Get-Acl/);
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
    assert.equal(candidateIsTrusted(executable, booster), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("an unsigned file is not trusted", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "brisa-trust-"));
  try {
    const executable = path.join(directory, "wiresock-client.exe");
    fs.writeFileSync(executable, "MZ");
    assert.equal(candidateIsTrusted(executable, path.join(directory, "wgbooster.dll")), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("the genuine vendor install is trusted", t => {
  if (!installed) return t.skip("WireSock is not installed on this machine");
  assert.equal(candidateIsTrusted(genuineExe, genuineBooster), true);
});
