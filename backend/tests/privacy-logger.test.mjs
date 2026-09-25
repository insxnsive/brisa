import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { transformSync } from "esbuild";

const here = path.dirname(fileURLToPath(import.meta.url));
const source = fs.readFileSync(path.join(here, "../src/network/logger.ts"), "utf8");
const compiled = transformSync(source, { loader: "ts", format: "cjs", platform: "node" }).code;
const module = { exports: {} };
new Function("require", "module", "exports", compiled)(createRequire(import.meta.url), module, module.exports);
const logger = module.exports;

test("diagnostic event fields reject arbitrary helper prose", () => {
  logger._resetForTests();
  const hostile = "person@example.test C:\\private\\person.conf https://proton.me/captcha?Token=fixture-query-937 PrivateKey=fixture-key-482";
  logger.logEvent("error", "proton", "confgen.complete", { operation_id: "fixture" }, {
    stderrTail: hostile, error: hostile, erro: hostile,
  });
  logger.warn("proton", "helper failed", { erro: hostile, server: hostile });
  const diagnostic = logger.getRecent();
  for (const sensitive of ["person@example.test", "person.conf", "fixture-query-937", "fixture-key-482"]) {
    assert.equal(diagnostic.includes(sensitive), false, sensitive);
  }
  assert.match(diagnostic, /confgen.complete/);
});

test("safe structured status fields survive redaction but sensitive numeric fields do not", () => {
  logger._resetForTests();
  logger.logEvent("info", "proton", "confgen.complete", { phase: "confgen" }, {
    durationMs: 42, code: 0, json: true, password: 937481,
  });
  const diagnostic = logger.getRecent();
  assert.match(diagnostic, /durationMs=42/);
  assert.match(diagnostic, /code=0/);
  assert.match(diagnostic, /json=true/);
  assert.equal(diagnostic.includes("937481"), false);
});

test("console interception omits arbitrary error text from persisted diagnostics", () => {
  logger._resetForTests();
  const target = { log() {}, warn() {}, error() {} };
  const restore = logger.patchConsole(target);
  try {
    target.error("helper person@example.test C:\\private\\person.conf Token=fixture-query-937");
    target.error("[fixtureSecretAlphabet] untrusted category");
    const diagnostic = logger.getRecent();
    assert.equal(diagnostic.includes("fixturesecretalphabet"), false);
    assert.match(diagnostic, /Console event/);
    assert.equal(diagnostic.includes("person@example.test"), false);
    assert.equal(diagnostic.includes("person.conf"), false);
    assert.equal(diagnostic.includes("fixture-query-937"), false);
  } finally { restore(); }
});
