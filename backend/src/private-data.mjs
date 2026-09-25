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
    const output = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', readAclScript], {
      input: JSON.stringify(paths), encoding: 'utf8', timeout: 10000, maxBuffer: 8 * 1024 * 1024,
      windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'],
      // PowerShell 7 paths inherited through Node can hide Windows PowerShell's
      // built-in ACL cmdlets. Rebuild this child's native module search paths.
      env: Object.fromEntries(Object.entries(process.env).filter(([key]) => key.toUpperCase() !== 'PSMODULEPATH')),
    });
    const parsed = JSON.parse(output);
    if (!Array.isArray(parsed.records) || parsed.records.length !== paths.length ||
        !validateAclRecords(parsed.records, parsed.currentUserSid)) throw new Error(PRIVATE_ERROR);
  } catch {
    throw new Error(PRIVATE_ERROR);
  }
}
