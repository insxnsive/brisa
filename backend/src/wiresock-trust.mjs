import { execFileSync } from "node:child_process";

/**
 * Identidade do fornecedor (The Anti-Cloud Corporation), fixada pelo sujeito do certificado e
 * não pela impressão digital: a renovação troca a impressão digital, não o emissor.
 */
export const WIRESOCK_SIGNER_SUBJECT = { cn: "The Anti-Cloud Corporation", o: "The Anti-Cloud Corporation" };

/** Escrita, exclusão, troca de dono ou de permissões: qualquer uma permite trocar o binário. */
const RISKY_FILE_RIGHTS = 852310;
const TRUSTED_SIDS = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];

const quote = value => `'${value.replace(/'/g, "''")}'`;

/**
 * Uma pasta que o próprio usuário pode escrever derrota qualquer checagem de versão: basta
 * colocar um wiresock-client.exe com versão alta. O par só é aceito se estiver assinado pelo
 * fornecedor e se nenhum diretório do caminho aceitar escrita de quem não é administrador.
 */
export function wireSockTrustScript(executable, booster) {
  const allowed = TRUSTED_SIDS.map(sid => `'${sid}'`).join(",");
  return `$ErrorActionPreference='Stop'
$risky = ${RISKY_FILE_RIGHTS}
$allowed = @(${allowed})
foreach ($p in @(${quote(executable)}, ${quote(booster)})) {
  $sig = Get-AuthenticodeSignature -LiteralPath $p
  if (($sig.Status -ne 'Valid') -or ($null -eq $sig.SignerCertificate)) { 'UNTRUSTED'; exit }
  $subject = $sig.SignerCertificate.Subject
  if (($subject -notmatch 'CN=${WIRESOCK_SIGNER_SUBJECT.cn}') -or ($subject -notmatch 'O=${WIRESOCK_SIGNER_SUBJECT.o}')) { 'UNTRUSTED'; exit }
  if (([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { 'UNTRUSTED'; exit }
}
$dir = [IO.Path]::GetDirectoryName(${quote(executable)})
while ($dir) {
  # A raiz do volume aceita criação de pastas por Usuários autenticados; isso não permite
  # trocar o binário, então a caminhada para antes dela.
  if ([IO.Path]::GetPathRoot($dir) -eq $dir) { break }
  if (((Get-Item -LiteralPath $dir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { 'UNTRUSTED'; exit }
  $acl = Get-Acl -LiteralPath $dir
  $owner = ''
  try { $owner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $owner = '' }
  if ($allowed -notcontains $owner) { 'UNTRUSTED'; exit }
  foreach ($rule in $acl.Access) {
    if ($rule.AccessControlType -ne 'Allow') { continue }
    $sid = ''
    try { $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { $sid = '' }
    if ($allowed -contains $sid) { continue }
    if (($rule.FileSystemRights -band $risky) -ne 0) { 'UNTRUSTED'; exit }
  }
  $dir = [IO.Path]::GetDirectoryName($dir)
}
'TRUSTED'`;
}

export function candidateIsTrusted(executable, booster, run = execFileSync) {
  if (process.platform !== "win32") return false;
  try {
    const output = run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", wireSockTrustScript(executable, booster)], {
      encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: 8000,
    }).trim();
    return output === "TRUSTED";
  } catch {
    return false;
  }
}
