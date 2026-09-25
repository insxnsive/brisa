import test from "node:test";
import assert from "node:assert/strict";
import { createRouteVerifier, verifyRouteEvidence } from "../src/route-verification.mjs";

const observation = (ip, source = "fixture") => ({ source, ip, country: "US" });
const result = (...observations) => ({ success: true, discordOk: true, observations });
test("host internet success is not proof of split-tunnel routing", () => {
  const direct = result(observation("192.0.2.1"));
  assert.deepEqual(verifyRouteEvidence(direct, direct), { verified: false, reason: "same_as_direct" });
  assert.deepEqual(verifyRouteEvidence(direct, result(observation("198.51.100.2"))), { verified: true, reason: "verified" });
  assert.equal(verifyRouteEvidence(direct, { ...result(observation("198.51.100.2")), discordOk: false }).verified, false);
  assert.equal(verifyRouteEvidence(null, result(observation("198.51.100.2"))).verified, false);
  assert.equal(verifyRouteEvidence(direct, result(observation("invalid-ip"))).verified, false);
  assert.equal(verifyRouteEvidence(direct, result(observation("198.51.100.2", "different-source"))).verified, false);
});
test("verification uses a separate helper path, never routes the account helper", async () => {
  const calls = [];
  const verifier = createRouteVerifier({ helperPath: "C:/fixture/resources/proton-confgen.exe", probePath: "C:/fixture/data/brisa-route-probe.exe",
    copy: async (...args) => calls.push(["copy", ...args]),
    runProbe: async exe => { calls.push(["probe", exe]); return result(observation(exe.includes("brisa-route") ? "198.51.100.2" : "192.0.2.1")); },
  });
  assert.equal(await verifier.prepare(), "C:/fixture/data/brisa-route-probe.exe");
  assert.deepEqual(await verifier.verify(), { verified: true, reason: "verified" });
  assert.equal(calls.filter(c => c[0] === "probe").length, 2);
  assert.notEqual(calls[1][1], calls[2][1]);
});
