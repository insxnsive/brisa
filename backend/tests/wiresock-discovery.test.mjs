import assert from "node:assert/strict";
import test from "node:test";
import { execFileSync } from "node:child_process";
import { WIRESOCK_LOCATION_SCRIPT, wireSockSearchRoots, wireSockCandidatePairs } from "../src/wiresock-discovery.mjs";

const custom = String.raw`D:\Programas\Wiresock`;
const exe = String.raw`D:\Programas\Wiresock\sdk\wiresock-client.exe`;
const dll = String.raw`D:\Programas\Wiresock\sdk\wgbooster.dll`;
const env = { ProgramFiles: String.raw`C:\Program Files` };
const fixtureFs = files => ({ existsSync: file => files.includes(file), readdirSync: () => [] });

for (const source of ["registry InstallLocation", "service executable directory", "process executable directory"]) {
  test(`custom SDK from ${source} is found without default-folder files`, () => {
    const location = source.startsWith("registry") ? custom : String.raw`D:\Programas\Wiresock\sdk`;
    const roots = wireSockSearchRoots(env, [location]);
    assert.equal(roots[0], location);
    assert.deepEqual(wireSockCandidatePairs(roots, fixtureFs([exe, dll])), [{ executable: exe, booster: dll }]);
  });
}

test("custom executable without its adjacent booster DLL is rejected", () => {
  assert.deepEqual(wireSockCandidatePairs([custom], fixtureFs([exe])), []);
});

test("installed locations are normalized, deduplicated, and reject relative or control-character paths", () => {
  assert.deepEqual(wireSockSearchRoots(env, [custom, custom.toLowerCase(), "relative", "D:\\bad\npath", "\\\\server\\share"]),
    [custom, String.raw`C:\Program Files\WireSock Secure Connect`]);
});


test("real Windows PowerShell extraction returns only installation directories from all sources", { skip: process.platform !== "win32" }, () => {
  const stubs = String.raw`
function Get-ItemProperty {
 param($Path,$ErrorAction)
 if($Path -like '*WOW6432Node*'){ return }
 @([pscustomobject]@{ DisplayName='WireSock Secure Connect SDK'; InstallLocation='D:\Programas\Wiresock' },
   [pscustomobject]@{ DisplayName='Unrelated app'; InstallLocation='D:\Unrelated' })
}
function Get-CimInstance {
 param($ClassName,$Filter,$ErrorAction)
 if($ClassName -eq 'Win32_Service'){
  return [pscustomobject]@{PathName='"E:\SDK custom\wiresock-client.exe" service -config "C:\private\must-not-return.conf"'}
 }
 return [pscustomobject]@{ExecutablePath='F:\WireSock\sdk\wiresock-client.exe'}
}
`;
  const locations = JSON.parse(execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", stubs + WIRESOCK_LOCATION_SCRIPT], { encoding: "utf8", windowsHide: true }));
  assert.deepEqual(locations, [String.raw`D:\Programas\Wiresock`, String.raw`E:\SDK custom`]);
});

test("discovery reads only machine-scope locations", () => {
  // HKCU and the running process path are writable without elevation, and whatever discovery
  // returns is launched as administrator.
  assert.doesNotMatch(WIRESOCK_LOCATION_SCRIPT, /HKCU:/);
  assert.doesNotMatch(WIRESOCK_LOCATION_SCRIPT, /Win32_Process/);
  assert.match(WIRESOCK_LOCATION_SCRIPT, /HKLM:/);
});
