import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import { findWindowsDiscordInstall } from "../src/network/windows-discord-install.ts";
import { sanitizeWireGuardConfig, formatAllowedApps } from "../src/network/vpn-types.ts";
import { discordAllowedApps } from "../src/discord-lifecycle.mjs";

for (const prefix of ["AllowedApps", "#@ws:AllowedApps"]) {
  test(`${prefix} for a different machine's 9259 is replaced by the installed 9258`, () => {
    const root = path.resolve("fixture", "Discord");
    const installed = path.join(root, "app-1.0.9258", "Discord.exe");
    const found = findWindowsDiscordInstall(root, "Discord", target => target === installed, () => ["app-1.0.9258"]);
    assert.equal(found.exePath, installed);
    const synthetic = `[Interface]\nPrivateKey = ${"A".repeat(43)}=\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = ${"B".repeat(43)}=\nAllowedIPs = 0.0.0.0/0\nEndpoint = 192.0.2.1:51820\n${prefix} = C:\\other-user\\Discord\\app-1.0.9259\\Discord.exe\n`;
    const scoped = discordAllowedApps([found.exePath]);
    const rebuilt = sanitizeWireGuardConfig(synthetic, formatAllowedApps([
      ...scoped.executables, ...scoped.appDirs, ...scoped.executableNames, ...scoped.updaterPaths,
    ]));
    assert.doesNotMatch(rebuilt, /9259|other-user/);
    assert.ok(rebuilt.includes(installed));
    assert.ok(rebuilt.includes("#@ws:AllowedApps = "));
  });
}

test("numeric discovery has no fixed minimum or maximum build", () => {
  const root = path.resolve("fixture", "Discord");
  const expected = path.join(root, "app-1.0.10000", "Discord.exe");
  const files = new Set([expected, path.join(root, "app-1.0.9258", "Discord.exe")]);
  assert.equal(findWindowsDiscordInstall(root, "Discord", p => files.has(p), () => ["app-1.0.9258", "app-1.0.10000"]).exePath, expected);
});
