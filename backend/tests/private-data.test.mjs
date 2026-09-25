import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import test from 'node:test';
import { assertPrivateDataTreeSync, secureNewPrivateDirectorySync, validateAclRecords, validateTrustedRecords } from '../src/private-data.mjs';

const scratch = process.env.TMPDIR || os.tmpdir();
const safeError = /Brisa data directory is not private\./;

test('ACL decision rejects grants to other principals, including inherited grants', () => {
  const user = 'S-1-5-21-1';
  const privateRecord = { path: 'fixture', owner: user, reparse: false, daclPresent: true, grants: [{ sid: user, type: 'Allow' }] };
  assert.equal(validateAclRecords([{ ...privateRecord, owner: 'S-1-5-21-2' }], user), false);
  assert.equal(validateAclRecords([{ ...privateRecord, owner: 'S-1-5-32-544' }], user), true);
  assert.equal(validateAclRecords([{ ...privateRecord, owner: undefined }], user), false);
  assert.equal(validateAclRecords([{ path: 'fixture', owner: user, reparse: false, daclPresent: true, grants: [{ sid: user, type: 'Allow' }, { sid: 'S-1-5-18', type: 'Allow' }] }], user), true);
  assert.equal(validateAclRecords([{ path: 'fixture', owner: user, reparse: false, daclPresent: true, grants: [{ sid: 'S-1-1-0', type: 'Allow', inherited: true }] }], user), false);
  assert.equal(validateAclRecords([{ path: 'fixture', owner: user, reparse: false, daclPresent: true, grants: [{ sid: 'S-1-5-32-545', type: 'Allow' }] }], user), false);
  assert.equal(validateAclRecords([{ path: 'fixture', owner: user, reparse: true, daclPresent: true, grants: [] }], user), false);
  assert.equal(validateAclRecords([{ path: 'fixture', owner: user, reparse: false, daclPresent: false, grants: [] }], user), false);
  assert.equal(validateAclRecords([], user), false);
});

test('legacy ownership decision rejects a foreign owner and reparse point while allowing inherited grants', () => {
  const user = 'S-1-5-21-1';
  const broad = { owner: user, reparse: false, daclPresent: true, grants: [{ sid: 'S-1-1-0', type: 'Allow', inherited: true }] };
  assert.equal(validateTrustedRecords([broad], user), true);
  assert.equal(validateTrustedRecords([{ ...broad, owner: 'S-1-5-21-2' }], user), false);
  assert.equal(validateTrustedRecords([{ ...broad, reparse: true }], user), false);
});

test('new private directory accepts an Administrators owner when running elevated', { skip: process.platform !== 'win32' }, t => {
  const elevated = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    '$p = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent(); $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)'],
  { encoding: 'utf8', windowsHide: true }).trim() === 'True';
  if (!elevated) return t.skip('Fixture cannot assign Administrators ownership without an elevated token.');
  const root = fs.mkdtempSync(path.join(scratch, 'brisa-admin-owner-'));
  try {
    const child = path.join(root, 'private');
    fs.mkdirSync(child);
    execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
      '$p = [Console]::In.ReadToEnd(); $a = Get-Acl -LiteralPath $p; $a.SetOwner([Security.Principal.SecurityIdentifier]::new("S-1-5-32-544")); Set-Acl -LiteralPath $p -AclObject $a'],
    { input: child, encoding: 'utf8', windowsHide: true });
    assert.doesNotThrow(() => secureNewPrivateDirectorySync(child));
    assert.doesNotThrow(() => assertPrivateDataTreeSync(child));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('NTFS readback accepts a private parent and child, rejects broad parent and file grants', { skip: process.platform !== 'win32' }, () => {
  fs.mkdirSync(scratch, { recursive: true });
  const root = fs.mkdtempSync(path.join(scratch, 'brisa-acl-'));
  const ps = (script, input) => execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { input, encoding: 'utf8', timeout: 5000, windowsHide: true });
  const sid = ps('[Security.Principal.WindowsIdentity]::GetCurrent().User.Value').trim();
  const icacls = (...args) => execFileSync('icacls.exe', args, { encoding: 'utf8', timeout: 5000, windowsHide: true });
  try {
    icacls(root, '/inheritance:r', '/grant:r', `*${sid}:(OI)(CI)F`, '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F');
    const child = path.join(root, 'profile.conf');
    fs.writeFileSync(child, 'synthetic fixture');
    assert.doesNotThrow(() => assertPrivateDataTreeSync(root));
    icacls(child, '/grant', '*S-1-1-0:R');
    assert.throws(() => assertPrivateDataTreeSync(root), safeError);
    icacls(child, '/remove', '*S-1-1-0');
    const junction = path.join(root, 'linked');
    fs.symlinkSync(root, junction, 'junction');
    assert.throws(() => assertPrivateDataTreeSync(root), safeError);
    fs.unlinkSync(junction);
    icacls(root, '/grant', '*S-1-5-32-545:R');
    assert.throws(() => assertPrivateDataTreeSync(root), safeError);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
