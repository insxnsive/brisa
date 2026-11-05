import assert from "node:assert/strict";
import test from "node:test";

import { formatAllowedApps, sanitizeWireGuardConfig, validateWireGuardConfig } from "../src/network/vpn-types.ts";

const KEY_A = `${"A".repeat(43)}=`;
const KEY_B = `${"B".repeat(43)}=`;
const KEY_C = `${"C".repeat(43)}=`;

// Verbatim shape of the Proton confgen template (tools/proton-confgen/internal/wireguard/config.go),
// including the metadata header comments that buildMetadata emits.
const protonShaped = `# ProtonVPN WireGuard Configuration
# Generated: 2026-01-01T00:00:00Z
# Device: Brisa
#
# Server Information:
# - Name: NL#1
# - Country: NL
# - City: Amsterdam
# - Tier: Free
# - Load: 20%
# - Score: 1.23
# Physical Server:
# - ID: abc123
# - Entry IP: 192.0.2.1
#
[Interface]
PrivateKey = ${KEY_A}
Address = 10.2.0.2/32, 2a07:b944::2:2/128
DNS = 10.2.0.1, 2a07:b944::2:1

[Peer]
PublicKey = ${KEY_B}
AllowedIPs = 0.0.0.0/0, ::/0
Endpoint = 192.0.2.1:51820
PersistentKeepalive = 10
`;

const protonShapedV4 = `[Interface]
PrivateKey = ${KEY_A}
Address = 10.2.0.2/32
DNS = 10.2.0.1

[Peer]
PublicKey = ${KEY_B}
AllowedIPs = 0.0.0.0/0
Endpoint = 192.0.2.1:51820
`;

const apps = formatAllowedApps([
  "C:\\Users\\u\\AppData\\Local\\Discord\\app-1.0.1\\Discord.exe",
  "C:\\Users\\u\\AppData\\Local\\Discord\\Update.exe",
]);

const withLine = (base, line) => base.replace("[Peer]", `[Peer]\n${line}`);
const withInterfaceLine = (base, line) => base.replace("[Interface]", `[Interface]\n${line}`);

test("a Proton-shaped profile still validates and sanitizes", () => {
  for (const fixture of [protonShaped, protonShaped.replace(/\r\n/g, "\n"), protonShapedV4]) {
    assert.equal(validateWireGuardConfig(fixture).valid, true);
    const sanitized = sanitizeWireGuardConfig(fixture, apps);
    for (const kept of ["PrivateKey", "Address", "PublicKey", "AllowedIPs", "Endpoint"]) {
      assert.match(sanitized, new RegExp(`^${kept} = `, "m"), kept);
    }
    if (fixture.includes("PersistentKeepalive")) assert.match(sanitized, /^PersistentKeepalive = 10\r?$/m);
    assert.doesNotMatch(sanitized, /^DNS\s*=/m);
    const comments = sanitized.split(/\r?\n/).filter(line => line.startsWith("#"));
    assert.deepEqual(comments, [`#@ws:AllowedApps = ${apps}`]);
    assert.equal((sanitized.match(/#@ws:/gi) ?? []).length, 1);
    assert.equal(validateWireGuardConfig(sanitized).valid, true, "the validator accepts its own sanitized output");
    assert.equal(sanitizeWireGuardConfig(sanitized, apps), sanitized, "sanitizing is idempotent");
  }
  // Dual-stack values survive the rebuild.
  const sanitized = sanitizeWireGuardConfig(protonShaped, apps);
  assert.match(sanitized, /^Address = 10\.2\.0\.2\/32, 2a07:b944::2:2\/128\r?$/m);
  assert.match(sanitized, /^AllowedIPs = 0\.0\.0\.0\/0, ::\/0\r?$/m);
});

test("every WireGuard hook is rejected with an app-owned code", () => {
  for (const key of ["PreUp", "PostUp", "PreDown", "PostDown", "SaveConfig", "Table"]) {
    for (const line of [`${key} = calc.exe`, `${key.toLowerCase()}=calc.exe`, `  ${key.toUpperCase()}  =  calc.exe`]) {
      for (const fixture of [withLine(protonShaped, line), withInterfaceLine(protonShaped, line)]) {
        const result = validateWireGuardConfig(fixture);
        assert.equal(result.valid, false, line);
        assert.equal(result.code, "PROFILE_HOOKS", line);
        assert.doesNotMatch(JSON.stringify(result), /calc\.exe/);
        assert.throws(() => sanitizeWireGuardConfig(fixture, apps), /PROFILE_HOOKS/);
      }
    }
  }
});

test("unknown keys, sections and duplicates are rejected", () => {
  const cases = [
    [withInterfaceLine(protonShaped, "Foo = 1"), "PROFILE_UNKNOWN_KEY"],
    [withInterfaceLine(protonShaped, "Jc = 5"), "PROFILE_UNKNOWN_KEY"],
    [withLine(protonShaped, "DNS = 10.2.0.1"), "PROFILE_UNKNOWN_KEY"],
    [`${protonShaped}\n[Extra]\nFoo = 1\n`, "PROFILE_UNKNOWN_SECTION"],
    [`${protonShaped}\n[Peer]\nPublicKey = ${KEY_C}\n`, "PROFILE_DUPLICATE_SECTION"],
    [withInterfaceLine(protonShaped, `PrivateKey = ${KEY_C}`), "PROFILE_DUPLICATE_KEY"],
  ];
  for (const [fixture, code] of cases) {
    const result = validateWireGuardConfig(fixture);
    assert.equal(result.valid, false, code);
    assert.equal(result.code, code);
    assert.throws(() => sanitizeWireGuardConfig(fixture, apps));
  }
});

test("a user-supplied app filter is replaced, never merged", () => {
  const variants = [
    "#@ws:AllowedApps=evil.exe",
    "#@ws:AllowedApps = evil.exe",
    "AllowedApps = evil.exe",
    "  #@WS:allowedapps = evil.exe",
    "# @ws: AllowedApps = evil.exe",
  ];
  for (const line of variants) {
    for (const fixture of [withLine(protonShaped, line), withInterfaceLine(protonShaped, line)]) {
      assert.equal(validateWireGuardConfig(fixture).valid, true, line);
      const sanitized = sanitizeWireGuardConfig(fixture, apps);
      assert.doesNotMatch(sanitized, /evil\.exe/, line);
      assert.deepEqual(sanitized.split(/\r?\n/).filter(entry => entry.startsWith("#")), [`#@ws:AllowedApps = ${apps}`]);
    }
  }
  const doubled = withLine(withLine(protonShaped, "#@ws:AllowedApps = evil.exe"), "AllowedApps = evil.exe");
  const sanitized = sanitizeWireGuardConfig(doubled, apps);
  assert.equal((sanitized.match(/#@ws:/gi) ?? []).length, 1);
  assert.doesNotMatch(sanitized, /evil\.exe/);
});

test("other WireSock directives are rejected", () => {
  for (const line of ["#@ws:Socks5Proxy = 1.2.3.4:1080", "#@ws:DisallowedApps = x", "#@ws:AllowedIPs = x", "#@foo"]) {
    const result = validateWireGuardConfig(withLine(protonShaped, line));
    assert.equal(result.valid, false, line);
    assert.equal(result.code, "PROFILE_DIRECTIVE", line);
    assert.throws(() => sanitizeWireGuardConfig(withLine(protonShaped, line), apps));
  }
});

test("line-break and inline-comment smuggling is rejected", () => {
  const cases = [
    withInterfaceLine(protonShaped, "Address = 10.2.0.2/32\rPostUp = calc"),
    `${protonShaped}\u0000`,
    `${protonShaped}\u2028PostUp = calc`,
    withLine(protonShaped, "Endpoint = 192.0.2.1:51820 #@ws:Socks5Proxy=x"),
    withLine(protonShaped, "PersistentKeepalive = 10; PostUp=x"),
  ];
  for (const fixture of cases) {
    const result = validateWireGuardConfig(fixture);
    assert.equal(result.valid, false);
    assert.ok(["PROFILE_CONTROL_CHARS", "PROFILE_BAD_VALUE"].includes(result.code), result.code);
    assert.throws(() => sanitizeWireGuardConfig(fixture, apps));
  }
});

test("allowed optional keys are carried through, invalid values are rejected", () => {
  const accepted = withLine(withInterfaceLine(protonShaped, "MTU = 1400\nListenPort = 51820"), `PresharedKey = ${KEY_C}`);
  assert.equal(validateWireGuardConfig(accepted).valid, true);
  const sanitized = sanitizeWireGuardConfig(accepted, apps);
  assert.match(sanitized, /^MTU = 1400\r?$/m);
  assert.match(sanitized, /^ListenPort = 51820\r?$/m);
  assert.match(sanitized, /^PresharedKey = /m);

  for (const line of ["MTU = 99999", "MTU = 0", "MTU = abc", "ListenPort = 0"]) {
    const result = validateWireGuardConfig(withInterfaceLine(protonShaped, line));
    assert.equal(result.valid, false, line);
    assert.equal(result.code, "PROFILE_BAD_VALUE", line);
  }
  const badPreshared = validateWireGuardConfig(withLine(protonShaped, "PresharedKey = not-a-key"));
  assert.equal(badPreshared.valid, false);
  assert.equal(badPreshared.code, "PROFILE_BAD_VALUE");

  // A hostname in DNS is a normal WireGuard value and must not refuse an import.
  const namedDnsSource = protonShaped.replace(/^DNS = .*$/m, "DNS = dns.example.com");
  assert.equal(validateWireGuardConfig(namedDnsSource).valid, true);
  assert.doesNotMatch(sanitizeWireGuardConfig(namedDnsSource, apps), /dns\.example\.com/);
  // A sane non-default MTU is accepted rather than second-guessed.
  assert.equal(validateWireGuardConfig(withInterfaceLine(protonShaped, "MTU = 1280")).valid, true);
});

test("no accepted profile can carry a hook or DNS into the sanitized output", () => {
  const fixtures = [protonShaped, protonShapedV4, withInterfaceLine(protonShaped, "MTU = 1400")];
  for (const fixture of fixtures) {
    assert.equal(validateWireGuardConfig(fixture).valid, true);
    const sanitized = sanitizeWireGuardConfig(fixture, apps);
    assert.doesNotMatch(sanitized, /^\s*(PreUp|PostUp|PreDown|PostDown|SaveConfig|Table|DNS)\b/im);
  }
});
