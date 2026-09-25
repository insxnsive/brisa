import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { prepareNativeDataStoreSync } from '../src/native-data-store.mjs';
import { helperSource } from '../src/native-storage-transaction.mjs';
import { assertPrivateDataTreeSync } from '../src/private-data.mjs';

const windows = { skip: process.platform !== 'win32' };
const scratch = process.env.TMPDIR || os.tmpdir();
const managed = ['state.json', 'proton-session.json', 'native-wiresock.conf', 'imported.conf', 'wireguard.conf'];
function fixture(run) {
  const root = fs.mkdtempSync(path.join(scratch, 'brisa-store-'));
  const base = path.join(root, 'Brisa');
  fs.mkdirSync(base);
  try { return run(base, root); } finally { fs.rmSync(root, { recursive: true, force: true }); }
}

test('fresh broad base creates a private NTFS child without traversing installer payload', windows, () => fixture((base, root) => {
  execFileSync('icacls.exe', [base, '/grant', '*S-1-5-32-545:(OI)(CI)M'], { encoding: 'utf8', windowsHide: true });
  assert.throws(() => assertPrivateDataTreeSync(base), /not private/i);
  const outside = path.join(root, 'outside');
  fs.mkdirSync(outside);
  fs.writeFileSync(path.join(outside, 'marker'), 'untouched');
  const current = path.join(base, 'current');
  fs.mkdirSync(current);
  fs.symlinkSync(outside, path.join(current, 'linked'), 'junction');
  const probe = () => { throw new Error('fresh stores must not probe'); };
  const dir = prepareNativeDataStoreSync(base, { probeLegacyTunnel: probe });
  assert.equal(dir, path.join(base, 'native-data'));
  assert.doesNotThrow(() => assertPrivateDataTreeSync(dir));
  assert.equal(fs.readFileSync(path.join(outside, 'marker'), 'utf8'), 'untouched');
  assert.equal(fs.lstatSync(path.join(current, 'linked')).isSymbolicLink(), true);
}));

test('migrates only exact legacy managed files as opaque bytes and is idempotent', windows, () => fixture(base => {
  const payloads = new Map(managed.map((name, i) => [name, Buffer.from([0, 255, i, 10])]));
  for (const [name, bytes] of payloads) fs.writeFileSync(path.join(base, name), bytes);
  fs.writeFileSync(path.join(base, 'settings.json'), 'native setting');
  fs.writeFileSync(path.join(base, 'stray.conf'), 'leave here');
  let probes = 0;
  const probeLegacyTunnel = oldConfig => { probes++; assert.equal(oldConfig, path.join(base, 'native-wiresock.conf')); return { reliable: true, active: false }; };
  const dir = prepareNativeDataStoreSync(base, { probeLegacyTunnel });
  for (const [name, bytes] of payloads) {
    assert.deepEqual(fs.readFileSync(path.join(dir, name)), bytes);
    assert.equal(fs.existsSync(path.join(base, name)), false);
  }
  assert.equal(fs.readFileSync(path.join(base, 'settings.json'), 'utf8'), 'native setting');
  assert.equal(fs.readFileSync(path.join(base, 'stray.conf'), 'utf8'), 'leave here');
  assert.doesNotThrow(() => assertPrivateDataTreeSync(dir));
  assert.equal(prepareNativeDataStoreSync(base, { probeLegacyTunnel: () => { throw new Error('unexpected'); } }), dir);
  assert.equal(probes, 1);
}));

test('conflicting destination preserves every legacy file', windows, () => fixture(base => {
  fs.writeFileSync(path.join(base, 'state.json'), 'old state');
  fs.writeFileSync(path.join(base, 'proton-session.json'), 'old session');
  const dir = prepareNativeDataStoreSync(base, { probeLegacyTunnel: () => ({ reliable: true, active: false }) });
  fs.writeFileSync(path.join(base, 'state.json'), 'different state');
  fs.writeFileSync(path.join(base, 'proton-session.json'), 'new session');
  assert.throws(() => prepareNativeDataStoreSync(base, { probeLegacyTunnel: () => ({ reliable: true, active: false }) }), /conflict/i);
  assert.equal(fs.readFileSync(path.join(base, 'state.json'), 'utf8'), 'different state');
  assert.equal(fs.readFileSync(path.join(base, 'proton-session.json'), 'utf8'), 'new session');
  assert.equal(fs.readFileSync(path.join(dir, 'state.json'), 'utf8'), 'old state');
}));

test('read-only matching destination retains its legacy original until it can be flushed', windows, () => fixture(base => {
  const original = Buffer.from([0, 255, 17, 10]);
  fs.writeFileSync(path.join(base, 'state.json'), original);
  const probeLegacyTunnel = () => ({ reliable: true, active: false });
  const dir = prepareNativeDataStoreSync(base, { probeLegacyTunnel });
  const destination = path.join(dir, 'state.json');
  fs.writeFileSync(path.join(base, 'state.json'), original);
  fs.chmodSync(destination, 0o444);
  try {
    assert.throws(() => prepareNativeDataStoreSync(base, { probeLegacyTunnel }), /migration failed safely/i);
    assert.deepEqual(fs.readFileSync(path.join(base, 'state.json')), original);
    assert.deepEqual(fs.readFileSync(destination), original);
  } finally {
    fs.chmodSync(destination, 0o666);
  }
  prepareNativeDataStoreSync(base, { probeLegacyTunnel });
  assert.equal(fs.existsSync(path.join(base, 'state.json')), false);
  assert.deepEqual(fs.readFileSync(destination), original);
}));

test('unsafe existing private directory is refused without hardening and retry succeeds after fixture cleanup', windows, () => fixture(base => {
  const dir = path.join(base, 'native-data');
  fs.mkdirSync(dir);
  execFileSync('icacls.exe', [dir, '/grant', '*S-1-5-32-545:(OI)(CI)M'], { encoding: 'utf8', windowsHide: true });
  fs.writeFileSync(path.join(base, 'state.json'), 'synthetic');
  const probeLegacyTunnel = () => ({ reliable: true, active: false });
  assert.throws(() => prepareNativeDataStoreSync(base, { probeLegacyTunnel }), /migration failed safely/i);
  assert.equal(fs.readFileSync(path.join(base, 'state.json'), 'utf8'), 'synthetic');
  assert.deepEqual(fs.readdirSync(dir), []);
  assert.throws(() => assertPrivateDataTreeSync(dir), /not private/i);
  fs.rmdirSync(dir);
  const recovered = prepareNativeDataStoreSync(base, { probeLegacyTunnel });
  assert.equal(fs.readFileSync(path.join(recovered, 'state.json'), 'utf8'), 'synthetic');
}));

test('failed second publish rolls back new destinations and keeps both legacy originals', windows, () => fixture(base => {
  fs.writeFileSync(path.join(base, 'state.json'), 'old state');
  fs.writeFileSync(path.join(base, 'proton-session.json'), 'old session');
  assert.throws(() => prepareNativeDataStoreSync(base, {
    probeLegacyTunnel: () => ({ reliable: true, active: false }),
    fixtureFailAfterPublish: 2,
  }), /migration failed safely/i);
  assert.equal(fs.readFileSync(path.join(base, 'state.json'), 'utf8'), 'old state');
  assert.equal(fs.readFileSync(path.join(base, 'proton-session.json'), 'utf8'), 'old session');
  assert.deepEqual(fs.readdirSync(path.join(base, 'native-data')), []);
  const recovered = prepareNativeDataStoreSync(base, { probeLegacyTunnel: () => ({ reliable: true, active: false }) });
  assert.equal(fs.readFileSync(path.join(recovered, 'state.json'), 'utf8'), 'old state');
  assert.equal(fs.existsSync(path.join(base, 'state.json')), false);
}));

test('failure after first source disposition keeps all destinations and resumes', windows, () => fixture(base => {
  const payloads = new Map([['state.json', Buffer.from([1, 0, 255])], ['proton-session.json', Buffer.from([2, 0, 254])]]);
  for (const [name, bytes] of payloads) fs.writeFileSync(path.join(base, name), bytes);
  const probeLegacyTunnel = () => ({ reliable: true, active: false });
  assert.throws(() => prepareNativeDataStoreSync(base, {
    probeLegacyTunnel,
    fixtureFailAfterDelete: 1,
  }), /migration failed safely/i);
  const dir = path.join(base, 'native-data');
  for (const [name, bytes] of payloads) assert.deepEqual(fs.readFileSync(path.join(dir, name)), bytes);
  assert.equal(fs.existsSync(path.join(base, 'state.json')), false);
  assert.deepEqual(fs.readFileSync(path.join(base, 'proton-session.json')), payloads.get('proton-session.json'));
  prepareNativeDataStoreSync(base, { probeLegacyTunnel });
  for (const [name, bytes] of payloads) {
    assert.deepEqual(fs.readFileSync(path.join(dir, name)), bytes);
    assert.equal(fs.existsSync(path.join(base, name)), false);
  }
}));

test('held directory and legacy file handles exclude replacement and writes', windows, async () => {
  const root = fs.mkdtempSync(path.join(scratch, 'brisa-locks-'));
  const base = path.join(root, 'Brisa');
  fs.mkdirSync(base);
  fs.mkdirSync(path.join(base, 'native-data'));
  const source = path.join(base, 'state.json');
  fs.writeFileSync(source, 'synthetic');
  const marker = path.join(root, 'locked.marker');
  const code = 'Add-Type -TypeDefinition $env:BRISA_STORAGE_SOURCE; [BrisaStorageTransaction]::HoldLocksForTest($env:BRISA_FIXTURE_BASE, $env:BRISA_FIXTURE_MARKER)';
  const child = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', code], {
    env: { ...process.env, BRISA_STORAGE_SOURCE: helperSource, BRISA_FIXTURE_BASE: base, BRISA_FIXTURE_MARKER: marker },
    windowsHide: true,
  });
  let stderr = '';
  child.stderr.on('data', chunk => { stderr += chunk; });
  const exit = new Promise(resolve => child.once('exit', resolve));
  try {
    const deadline = Date.now() + 5000;
    while (!fs.existsSync(marker) && Date.now() < deadline && child.exitCode === null)
      await new Promise(resolve => setTimeout(resolve, 25));
    assert.equal(fs.existsSync(marker), true, stderr);
    assert.throws(() => fs.renameSync(source, path.join(base, 'moved.json')), /EPERM|EACCES|EBUSY/i);
    assert.throws(() => fs.writeFileSync(source, 'changed'), /EPERM|EACCES|EBUSY/i);
    assert.throws(() => fs.renameSync(path.join(base, 'native-data'), path.join(base, 'replaced')), /EPERM|EACCES|EBUSY/i);
    assert.throws(() => fs.renameSync(base, path.join(root, 'moved-base')), /EPERM|EACCES|EBUSY/i);
  } finally {
    if (fs.existsSync(marker)) fs.rmSync(marker);
    const code = await exit;
    assert.equal(code, 0, stderr);
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('active and unknown WireSock state refuse migration without changing legacy bytes', windows, () => {
  for (const status of [{ reliable: true, active: true }, { reliable: false, active: false }]) fixture(base => {
    const original = Buffer.from([7, 0, 255]);
    fs.writeFileSync(path.join(base, 'native-wiresock.conf'), original);
    assert.throws(() => prepareNativeDataStoreSync(base, { probeLegacyTunnel: () => status }), /WireSock/i);
    assert.deepEqual(fs.readFileSync(path.join(base, 'native-wiresock.conf')), original);
    assert.equal(fs.existsSync(path.join(base, 'native-data', 'native-wiresock.conf')), false);
  });
});

test('rejects legacy reparse points and private-store reparse boundary', windows, () => fixture((base, root) => {
  const outside = path.join(root, 'outside');
  fs.mkdirSync(outside);
  fs.writeFileSync(path.join(outside, 'marker'), 'outside');
  fs.symlinkSync(outside, path.join(base, 'native-wiresock.conf'), 'junction');
  assert.throws(() => prepareNativeDataStoreSync(base, { probeLegacyTunnel: () => ({ reliable: true, active: false }) }), /private|reparse/i);
  fs.unlinkSync(path.join(base, 'native-wiresock.conf'));
  if (fs.existsSync(path.join(base, 'native-data'))) fs.rmSync(path.join(base, 'native-data'), { recursive: true });
  fs.symlinkSync(root, path.join(base, 'native-data'), 'junction');
  assert.throws(() => prepareNativeDataStoreSync(base), /private|reparse/i);
  assert.equal(fs.readFileSync(path.join(outside, 'marker'), 'utf8'), 'outside');
}));
