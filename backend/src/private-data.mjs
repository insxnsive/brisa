import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const PRIVATE_ERROR = 'Brisa data directory is not private.';
const allowedSystemSids = new Set(['S-1-5-18', 'S-1-5-32-544']);

// Inspect every allow ACE, including inherited ACEs. A deny ACE never makes
// an unrelated allow ACE safe, so it does not relax this boundary.
export function validateAclRecords(records, currentUserSid) {
  if (!Array.isArray(records) || records.length === 0 || typeof currentUserSid !== 'string' || !currentUserSid) return false;
  return records.every(record => record && (record.owner === currentUserSid || allowedSystemSids.has(record.owner)) && record.reparse === false && record.daclPresent === true && Array.isArray(record.grants) &&
    record.grants.every(grant => grant && typeof grant.sid === 'string' &&
      (grant.type !== 'Allow' || grant.sid === currentUserSid || allowedSystemSids.has(grant.sid))));
}

export function validateTrustedRecords(records, currentUserSid) {
  return Array.isArray(records) && records.length > 0 && typeof currentUserSid === 'string' && Boolean(currentUserSid) &&
    records.every(record => record &&
      (record.owner === currentUserSid || allowedSystemSids.has(record.owner)) &&
      record.reparse === false && record.daclPresent === true);
}

const readAclScript = `
$ErrorActionPreference = 'Stop'
$paths = [Console]::In.ReadToEnd() | ConvertFrom-Json
$current = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$result = foreach ($entry in $paths) {
  $item = Get-Item -LiteralPath $entry -Force -ErrorAction Stop
  $acl = Get-Acl -LiteralPath $entry -ErrorAction Stop
  $raw = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
  $grants = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
    @{ sid = $_.IdentityReference.Value; type = $_.AccessControlType.ToString(); inherited = $_.IsInherited }
  })
  @{ path = $entry; owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value; reparse = [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint); daclPresent = ($null -ne $raw.DiscretionaryAcl); grants = $grants }
}
@{ currentUserSid = $current; records = @($result) } | ConvertTo-Json -Depth 6 -Compress
`;

function readAclRecordsSync(paths) {
  const output = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', readAclScript], {
    input: JSON.stringify(paths), encoding: 'utf8', timeout: 10000, maxBuffer: 8 * 1024 * 1024,
    windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'],
    env: Object.fromEntries(Object.entries(process.env).filter(([key]) => key.toUpperCase() !== 'PSMODULEPATH')),
  });
  const parsed = JSON.parse(output);
  if (!Array.isArray(parsed.records) || parsed.records.length !== paths.length) throw new Error(PRIVATE_ERROR);
  return parsed;
}

// The host base may inherit installer grants. For that boundary and exact
// legacy files, only ownership and reparse safety are required before copying.
export function assertTrustedDataPathsSync(paths) {
  if (process.platform !== 'win32') throw new Error('Brisa private data requires Windows NTFS ACLs.');
  try {
    for (const target of paths) {
      let ancestor = path.resolve(target);
      while (ancestor !== path.dirname(ancestor)) {
        if (fs.lstatSync(ancestor).isSymbolicLink()) throw new Error(PRIVATE_ERROR);
        ancestor = path.dirname(ancestor);
      }
    }
    const { records, currentUserSid } = readAclRecordsSync(paths);
    if (!validateTrustedRecords(records, currentUserSid)) throw new Error(PRIVATE_ERROR);
  } catch { throw new Error(PRIVATE_ERROR); }
}

const secureDirectoryScript = `
$ErrorActionPreference = 'Stop'
$target = [Console]::In.ReadToEnd()
$item = Get-Item -LiteralPath $target -Force -ErrorAction Stop
if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Invalid directory' }
$user = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = Get-Acl -LiteralPath $target -ErrorAction Stop
if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @($user.Value, 'S-1-5-18', 'S-1-5-32-544')) { throw 'Untrusted owner' }
$acl.SetAccessRuleProtection($true, $false)
foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRuleAll($rule) }
foreach ($sid in @($user.Value, 'S-1-5-18', 'S-1-5-32-544')) {
  $identity = [Security.Principal.SecurityIdentifier]::new($sid)
  $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity, [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
  [void]$acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $target -AclObject $acl -ErrorAction Stop
`;

const secureFileScript = secureDirectoryScript
  .replace("if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))", "if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))")
  .replace("[Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'", '[Security.AccessControl.InheritanceFlags]::None');

export function securePrivateFileSync(target) {
  if (process.platform !== 'win32') throw new Error('Brisa private data requires Windows NTFS ACLs.');
  try {
    execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', secureFileScript], {
      input: target, encoding: 'utf8', timeout: 10000, windowsHide: true,
      stdio: ['pipe', 'pipe', 'ignore'],
      env: Object.fromEntries(Object.entries(process.env).filter(([key]) => key.toUpperCase() !== 'PSMODULEPATH')),
    });
    assertPrivateDataTreeSync(target);
  } catch { throw new Error(PRIVATE_ERROR); }
}

export function secureNewPrivateDirectorySync(target) {
  if (process.platform !== 'win32') throw new Error('Brisa private data requires Windows NTFS ACLs.');
  try {
    execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', secureDirectoryScript], {
      input: target, encoding: 'utf8', timeout: 10000, windowsHide: true,
      stdio: ['pipe', 'pipe', 'ignore'],
      env: Object.fromEntries(Object.entries(process.env).filter(([key]) => key.toUpperCase() !== 'PSMODULEPATH')),
    });
    assertPrivateDataTreeSync(target);
  } catch { throw new Error(PRIVATE_ERROR); }
}

export function assertPrivateDataTreeSync(root) {
  if (process.platform !== 'win32') throw new Error('Brisa private data requires Windows NTFS ACLs.');
  try {
    const resolved = path.resolve(root);
    const paths = [];
    // Also reject junctions or symlinks in ancestors of the managed root.
    let ancestor = resolved;
    while (ancestor !== path.dirname(ancestor)) {
      const stat = fs.lstatSync(ancestor);
      if (stat.isSymbolicLink()) throw new Error(PRIVATE_ERROR);
      ancestor = path.dirname(ancestor);
    }
    const visit = target => {
      if (paths.length >= 10000) throw new Error(PRIVATE_ERROR);
      const stat = fs.lstatSync(target);
      if (stat.isSymbolicLink() || (!stat.isDirectory() && !stat.isFile())) throw new Error(PRIVATE_ERROR);
      paths.push(target);
      if (stat.isDirectory()) for (const entry of fs.readdirSync(target)) visit(path.join(target, entry));
    };
    visit(resolved);
    const parsed = readAclRecordsSync(paths);
    if (!validateAclRecords(parsed.records, parsed.currentUserSid)) throw new Error(PRIVATE_ERROR);
  } catch {
    throw new Error(PRIVATE_ERROR);
  }
}
