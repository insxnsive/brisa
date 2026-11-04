import assert from "node:assert/strict";
import test from "node:test";
import { renderSupportLog } from "../src/support-log.mjs";

const at = new Date("2026-03-04T05:06:07.000Z");
const event = "2026-03-04T05:00:00.000Z starting-tunnel START_CLIENT_EXITED exitCode=1 timedOut=false";

test("the log opens with the plain-language header and a UTC stamp", () => {
  const text = renderSupportLog({ generatedAt: at, events: [] });
  const [first] = text.split("\n");
  assert.equal(first, "Brisa log. Generated 2026-03-04T05:06:07.000Z. Contains no account names, passwords, file paths or IP addresses.");
});

test("only app-owned codes pass, whatever their shape", () => {
  const text = renderSupportLog({
    generatedAt: at,
    facts: { discordFlavour: "PTB", mode: "proton", protocol: "wireguard_udp", theme: "dark", phase: "connected" },
  });
  assert.match(text, /^Discord flavour: PTB$/m);
  assert.match(text, /^Mode: PROTON$/m);
  assert.match(text, /^Protocol: WIREGUARD_UDP$/m);
  assert.match(text, /^Theme: DARK$/m);
  assert.match(text, /^Phase: CONNECTED$/m);

  const bad = renderSupportLog({
    generatedAt: at,
    facts: { discordFlavour: "NATE", mode: "someone", theme: "C:\\Users\\nate", phase: "NATE" },
  });
  assert.doesNotMatch(bad, /NATE/);
  assert.doesNotMatch(bad, /someone/);
  assert.doesNotMatch(bad, /Users/);
});

test("the SDK version is four integers, never a dotted string", () => {
  assert.match(renderSupportLog({ generatedAt: at, facts: { sdkMajor: 1, sdkMinor: 2, sdkBuild: 3, sdkRevision: 4 } }),
    /^WireSock SDK version: 1\.2\.3\.4$/m);
  const dotted = renderSupportLog({ generatedAt: at, facts: { sdkVersion: "1.2.3.4" } });
  assert.doesNotMatch(dotted, /SDK version/);
  const partial = renderSupportLog({ generatedAt: at, facts: { sdkMajor: 1, sdkMinor: 2 } });
  assert.doesNotMatch(partial, /SDK version/, "a partial version is omitted instead of guessed");
});

test("elevation, trust verdict and route country are booleans and codes, nothing else", () => {
  const text = renderSupportLog({
    generatedAt: at,
    facts: { elevated: false, trustVerdict: "UNTRUSTED", routeCountry: "HT", routeCheck: "probe_failed", stage: "idle" },
  });
  assert.match(text, /^Elevated: false$/m);
  assert.match(text, /^WireSock trust: UNTRUSTED$/m);
  assert.match(text, /^Route country: HT$/m);
  assert.match(text, /^Route check: probe_failed$/m);
  assert.match(text, /^Stage: idle$/m);
  assert.doesNotMatch(text, /Route country: Brasil/, "a full country name is not accepted");
});

test("events are kept only when they are already the redacted shape", () => {
  const text = renderSupportLog({
    generatedAt: at,
    events: [event, "2026-03-04T05:00:00.000Z startup OK_CODE path=C:\\Users\\nate", "free text about a tunnel"],
  });
  assert.match(text, new RegExp(event.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  assert.doesNotMatch(text, /Users/);
  assert.doesNotMatch(text, /free text/);
});

test("a log with no facts still reads as a log", () => {
  const text = renderSupportLog({ generatedAt: at });
  assert.match(text, /^Events \(this session\): none recorded$/m);
  assert.doesNotMatch(text, /^Elevated: /m);
  assert.doesNotMatch(text, /^Mode: /m);
  assert.doesNotMatch(text, /undefined|null/, "a missing fact is omitted, never printed");
});
