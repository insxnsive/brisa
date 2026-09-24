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

test("native WireSock start uses direct application mode without global service or process mutation", () => {
  assert.match(source, /['\"]run['\"]/);
  assert.doesNotMatch(source, /\binstall\s+-|Stop-Service|taskkill|execFileSync|Get-Process[^\n]*Stop-Process/);
  assert.match(source, /Stop-Process -Id \$child\.Id/);
  assert.match(source, /cancel/i);
  assert.ok(source.includes("elevatedPowerShellFileArgs(scriptPath, resultPath)"));
  assert.ok(source.includes("file === cancelPath && cancellationMarked"));
  assert.match(source, /Get-CimInstance Win32_(?:Service|Process)/);
});
