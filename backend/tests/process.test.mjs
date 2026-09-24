import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

function runFrames(frames) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [path.join(root, "dist", "backend-fixture.cjs")], { stdio: ["pipe", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    child.stdout.setEncoding("utf8");
    child.stderr.setEncoding("utf8");
    child.stdout.on("data", chunk => { stdout += chunk; });
    child.stderr.on("data", chunk => { stderr += chunk; });
    child.on("error", reject);
    child.on("close", code => code === 0 ? resolve({ stdout, stderr }) : reject(new Error(`fixture exited ${code}`)));
    for (const frame of frames) child.stdin.write(frame);
    child.stdin.end();
  });
}

test("actual bundled process returns the snapshot NDJSON fixture contract", async () => {
  const { stdout, stderr } = await runFrames([JSON.stringify({ id: "snap-1", command: "snapshot", payload: {} }) + "\n"]);
  assert.equal(stderr, "");
  assert.deepEqual(JSON.parse(stdout.trim()), {
    id: "snap-1", ok: true,
    result: { connected: false, externalTunnel: false, reliable: true, signedIn: true, username: "fixture-user", route: null, mode: "proton" },
  });
});

test("actual bundled process rejects schema and oversized frames without echoing input", async () => {
  const secret = "never-echo-this-secret";
  const { stdout } = await runFrames([
    JSON.stringify({ id: "bad", command: "login", payload: { username: "u", password: secret }, extra: true }) + "\n",
    "x".repeat(1024 * 1024 + 1) + secret + "\n",
  ]);
  const responses = stdout.trim().split(/\r?\n/).map(line => JSON.parse(line));
  assert.equal(responses[0].ok, false);
  assert.equal(responses[1].error, "Frame exceeds maximum size.");
  assert.equal(stdout.includes(secret), false);
});
