import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const backendRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceRoot = path.join(backendRoot, "src");
const forbidden = [
  "golive-gui",
  "goLiveBypass",
  "vpn-linux",
];

function sourceFiles(root) {
  return fs.readdirSync(root, { withFileTypes: true }).flatMap(entry => {
    const target = path.join(root, entry.name);
    return entry.isDirectory() ? sourceFiles(target) : /\.(?:[cm]?[jt]s)$/.test(entry.name) ? [target] : [];
  });
}

test("backend source is a self-contained native Windows dependency closure", () => {
  const violations = [];
  for (const file of sourceFiles(sourceRoot)) {
    const relative = path.relative(backendRoot, file).replaceAll("\\", "/");
    const source = fs.readFileSync(file, "utf8");
    for (const token of forbidden) {
      if (source.includes(token)) violations.push(`${relative}: ${token}`);
    }
    for (const match of source.matchAll(/(?:from\s+|import\s*\(|require\s*\()\s*["']([^"']+)["']/g)) {
      const specifier = match[1];
      assert.notEqual(specifier, "electron", `${relative} imports Electron`);
      if (!specifier.startsWith(".")) continue;
      const resolved = path.resolve(path.dirname(file), specifier);
      assert.ok(resolved === sourceRoot || resolved.startsWith(`${sourceRoot}${path.sep}`), `${relative} imports outside backend/src: ${specifier}`);
    }
  }
  assert.deepEqual(violations, []);
});
