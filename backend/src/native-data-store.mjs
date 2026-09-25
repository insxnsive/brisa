import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { helperSource } from './native-storage-transaction.mjs';
import { assertTrustedDataPathsSync } from './private-data.mjs';

const names = Object.freeze(['state.json', 'proton-session.json', 'native-wiresock.conf', 'imported.conf', 'wireguard.conf']);
const error = 'Brisa private data migration failed safely.';
const script = `
$ErrorActionPreference = 'Stop'
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
# Resolve references from the running Windows PowerShell framework. Relative
# defaults can bind to Brisa's bundled .NET System.dll in the app working folder.
Add-Type -TypeDefinition ([string]$request.source) -ReferencedAssemblies @([object].Assembly.Location, [System.Collections.Generic.Stack[int]].Assembly.Location)
[BrisaStorageTransaction]::Run([string]$request.base, [bool]$request.allowed, [int]$request.failAfter, [int]$request.failAfterDelete)
`;

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
  assertTrustedDataPathsSync([hostBase]);
  const legacy = exactLegacyNames(hostBase);
  let allowed = false;
  if (legacy.length) {
    if (typeof probeLegacyTunnel !== 'function') throw new Error('WireSock migration inspection is unavailable.');
    const status = probeLegacyTunnel(path.join(hostBase, 'native-wiresock.conf'));
    if (!status || status.reliable !== true || status.active !== false)
      throw new Error('WireSock migration was deferred because a tunnel may be active.');
    allowed = true;
  }
  try {
    execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], {
      input: JSON.stringify({ base: hostBase, allowed, failAfter: fixtureFailAfterPublish, failAfterDelete: fixtureFailAfterDelete, source: helperSource }), encoding: 'utf8', timeout: 30000,
      windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
      env: Object.fromEntries(Object.entries(process.env).filter(([key]) => key.toUpperCase() !== 'PSMODULEPATH')),
    });
  } catch (cause) {
    const detail = String(cause?.stderr || '');
    if (detail.includes('migration conflict')) throw new Error('Brisa private data migration conflict.', { cause });
    throw new Error(error, { cause });
  }
  return path.join(hostBase, 'native-data');
}
