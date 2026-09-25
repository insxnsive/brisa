import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const source = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../src/native-wiresock-start.ts"), "utf8");
const production = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../src/production.ts"), "utf8");

test("production blocks mutations when the current Windows token is not elevated", () => {
  assert.ok(production.includes("let elevated = false"));
  assert.ok(production.includes("WindowsBuiltInRole]::Administrator"));
  assert.ok(production.includes("elevated ? inspectWireSockAsync(configPath)"));
  assert.ok(production.includes('reason: "Run Brisa as administrator'));
  assert.ok(production.includes("active: false, owned: false, reliable: false"));
});

test("production wires scoped Discord restart, route proof and post-start settling", () => {
  assert.match(production, /stopDiscord:/);
  assert.match(production, /validateDiscordApps: trustedApps/);
  assert.match(production, /createDiscordSelectionGuard\(discordExecutables\)/);
  assert.match(production, /cleanup \? selectionGuard\.assertCleanup : trustedApps/);
  assert.match(production, /discordRunning:/);
  assert.match(production, /verifyRoute:/);
  assert.match(production, /discordAllowedApps\(apps\)/);
  assert.match(production, /await delay\(2_000/);
  assert.doesNotMatch(production, /detached: true/);
});

test("native start prepares WireSock on demand before launching the owned tunnel", () => {
  assert.match(source, /await ensureWireSockInstalled\(/);
  assert.match(source, /findWireSockCandidate\(\)/);
  assert.match(source, /A compatible WireSock SDK installation is required\./);
});

test("WireSock setup launches the verified vendor installer visibly, never silently", () => {
  const vpnWindows = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../src/network/vpn-windows.ts"), "utf8");
  assert.match(vpnWindows, /WIRESOCK_INSTALLER_SHA256/);
  assert.match(vpnWindows, /Start-Process -FilePath .* -Verb RunAs -WindowStyle Normal/);
  assert.doesNotMatch(vpnWindows, /ArgumentList @\('\/quiet','\/norestart'\)/);
});

test("native WireSock start uses direct application mode without global service or process mutation", () => {
  assert.match(source, /['\"]run['\"]/);
  assert.doesNotMatch(source, /\binstall\s+-|Stop-Service|taskkill|execFileSync|Get-Process[^\n]*Stop-Process/);
  assert.match(source, /Stop-Process -Id \$child\.Id/);
  assert.match(source, /cancel/i);
  assert.ok(source.includes("elevatedPowerShellFileArgs(scriptPath, resultPath)"));
  assert.ok(source.includes("file === cancelPath && cancellationMarked"));
  assert.match(source, /Get-CimInstance Win32_(?:Service|Process)/);
});
