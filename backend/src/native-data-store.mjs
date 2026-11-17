import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { helperSource } from './native-storage-transaction.mjs';
import { powerShellCommandArgs } from './network/powershell.ts';

const names = Object.freeze(['state.json', 'proton-session.json', 'native-wiresock.conf', 'imported.conf', 'wireguard.conf']);
const error = 'Brisa private data migration failed safely.';
const notPrivate = 'Brisa data directory is not private.';
const HOST_VERIFIED = 'BRISA_STORAGE_VERIFIED';
// The storage helper's own refusals of a foreign owner, a missing DACL or a reparse point. The
// helper checks the base while holding every ancestor open (LockAncestors), which is what the
// separate ACL read used to check first, so these keep the same startup error.
const NOT_PRIVATE_DETAILS = Object.freeze([
  'Foreign storage owner.',
  'Unprotected storage object.',
  'Storage object is not a regular file or directory.',
]);
const script = `
$ErrorActionPreference = 'Stop'
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
# Resolve references from the running Windows PowerShell framework. Relative
# defaults can bind to Brisa's bundled .NET System.dll in the app working folder.
Add-Type -TypeDefinition ([string]$request.source) -ReferencedAssemblies @([object].Assembly.Location, [System.Collections.Generic.Stack[int]].Assembly.Location)
[BrisaStorageTransaction]::Run([string]$request.base, [bool]$request.allowed, [int]$request.failAfter, [int]$request.failAfterDelete)
`;

// No symlink or junction at or above the store. A Node check, before anything reads the folder or
// asks about a legacy tunnel; the helper repeats it with handles held.
function assertNoLinkedAncestor(hostBase) {
  try {
    for (let ancestor = hostBase; ancestor !== path.dirname(ancestor); ancestor = path.dirname(ancestor))
      if (fs.lstatSync(ancestor).isSymbolicLink()) throw new Error(notPrivate);
  } catch { throw new Error(notPrivate); }
}

// Brisa.exe ran this same helper in-process for exactly this base, without migration, just before
// starting node (BackendClient.VerifyStorage). The value comes from the parent, like BRISA_DATA_DIR.
// Anything else (tests, the startup smoke, another base, a legacy file) runs the helper here.
function verifiedByHost(hostBase) {
  const value = process.env[HOST_VERIFIED];
  if (typeof value !== 'string' || !path.isAbsolute(value) || path.resolve(value).toLowerCase() !== hostBase.toLowerCase()) return false;
  try {
    const stat = fs.lstatSync(path.join(hostBase, 'native-data'));
    return stat.isDirectory() && !stat.isSymbolicLink();
  } catch { return false; }
}

function exactLegacyNames(base) {
  const entries = fs.readdirSync(base);
  return names.filter(name => entries.includes(name));
}

// The helper holds non-deletable handles for the ancestor chain, private
// directory, and exact legacy files for the entire copy and removal sequence.
export function prepareNativeDataStoreSync(base, { probeLegacyTunnel, fixtureFailAfterPublish = 0, fixtureFailAfterDelete = 0 } = {}) {
  if (process.platform !== 'win32' || !path.isAbsolute(base)) throw new Error(error);
  const hostBase = path.resolve(base);
  fs.mkdirSync(hostBase, { recursive: true });
  assertNoLinkedAncestor(hostBase);
  const legacy = exactLegacyNames(hostBase);
  if (!legacy.length && verifiedByHost(hostBase)) return path.join(hostBase, 'native-data');
  let allowed = false;
  if (legacy.length) {
    if (typeof probeLegacyTunnel !== 'function') throw new Error('WireSock migration inspection is unavailable.');
    const status = probeLegacyTunnel(path.join(hostBase, 'native-wiresock.conf'));
    if (!status || status.reliable !== true || status.active !== false)
      throw new Error('WireSock migration was deferred because a tunnel may be active.');
    allowed = true;
  }
  try {
    execFileSync('powershell.exe', powerShellCommandArgs(script), {
      input: JSON.stringify({ base: hostBase, allowed, failAfter: fixtureFailAfterPublish, failAfterDelete: fixtureFailAfterDelete, source: helperSource }), encoding: 'utf8', timeout: 30000,
      windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
      env: Object.fromEntries(Object.entries(process.env).filter(([key]) => key.toUpperCase() !== 'PSMODULEPATH')),
    });
  } catch (cause) {
    const detail = String(cause?.stderr || '');
    if (detail.includes('migration conflict')) throw new Error('Brisa private data migration conflict.', { cause });
    if (NOT_PRIVATE_DETAILS.some(text => detail.includes(text))) throw new Error(notPrivate, { cause });
    throw new Error(error, { cause });
  }
  return path.join(hostBase, 'native-data');
}
