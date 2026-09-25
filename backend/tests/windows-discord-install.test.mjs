import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { findWindowsDiscordInstall } from "../src/network/windows-discord-install.ts";

test("official Discord versioned client takes priority over its short-lived root launcher", () => {
  const root = path.resolve("fixture", "Discord");
  const client = path.join(root, "app-1.0.20", "Discord.exe");
  const files = new Set([path.join(root, "Discord.exe"), client, path.join(root, "app-1.0.9", "Discord.exe")]);
  const result = findWindowsDiscordInstall(root, "Discord", p => files.has(p), () => ["app-1.0.9", "app-1.0.20"]);
  assert.equal(result.exePath, client);
  assert.equal(result.appDir, path.dirname(client));
});

test("a standalone alternative still prefers its root executable", () => {
  const root = path.resolve("fixture", "Equibop");
  const exe = path.join(root, "Equibop.exe");
  const result = findWindowsDiscordInstall(root, "Equibop", p => p === exe || p.endsWith("app-0.1"+path.sep+"Equibop.exe"), () => ["app-0.1"]);
  assert.equal(result.exePath, exe);
});

test("official Discord without a resolved versioned client fails closed", () => {
  const root = path.resolve("fixture", "Discord");
  const exe = path.join(root, "Discord.exe");
  assert.equal(findWindowsDiscordInstall(root, "Discord", p => p === exe, () => []), null);
});
