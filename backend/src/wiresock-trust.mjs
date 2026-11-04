import { execFileSync } from "node:child_process";

/**
 * Identidade do fornecedor (The Anti-Cloud Corporation), fixada pelo sujeito do certificado e
 * não pela impressão digital: a renovação troca a impressão digital, não o emissor.
 */
export const WIRESOCK_SIGNER_SUBJECT = { cn: "The Anti-Cloud Corporation", o: "The Anti-Cloud Corporation" };

/** Escrita, exclusão, troca de dono ou de permissões: qualquer uma permite trocar o binário.
 * GENERIC_ALL/GENERIC_WRITE também: eles não têm bit em comum com os direitos específicos. */
const RISKY_FILE_RIGHTS = "0xD0156 -bor 0x10000000 -bor 0x40000000";
const TRUSTED_SIDS = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];

const quote = value => `'${value.replace(/'/g, "''")}'`;

/**
 * Uma pasta que o próprio usuário pode escrever derrota qualquer checagem de versão: basta
 * colocar um wiresock-client.exe com versão alta. Os dois arquivos e todos os diretórios do
 * caminho precisam ter dono administrativo e nenhuma permissão de escrita para quem não é
 * administrador, sem pontos de reparse que redirecionem o caminho depois da checagem.
 */
export function wireSockTrustScript(executable, booster) {
  const allowed = TRUSTED_SIDS.map(sid => `'${sid}'`).join(",");
  return `$ErrorActionPreference='Stop'
$risky = ${RISKY_FILE_RIGHTS}
$allowed = @(${allowed})
$inheritOnly = [Security.AccessControl.PropagationFlags]::InheritOnly
function Test-BrisaAcl([string]$path) {
  if (((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $false }
  $acl = Get-Acl -LiteralPath $path
  $owner = ''
  try { $owner = (New-Object Security.Principal.NTAccount($acl.Owner)).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $owner = '' }
  if ($allowed -notcontains $owner) { return $false }
  foreach ($rule in $acl.Access) {
    if ($rule.AccessControlType -ne 'Allow') { continue }
    # Um ACE só de herança não se aplica ao próprio objeto; o CREATOR OWNER herdado de
    # Program Files é GENERIC_ALL e rejeitaria toda instalação legítima.
    if ($rule.PropagationFlags -band $inheritOnly) { continue }
    $sid = ''
    try { $sid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { $sid = '' }
    if ($allowed -contains $sid) { continue }
    if (($rule.FileSystemRights -band $risky) -ne 0) { return $false }
  }
  return $true
}
foreach ($p in @(${quote(executable)}, ${quote(booster)})) {
  $sig = Get-AuthenticodeSignature -LiteralPath $p
  if (($sig.Status -ne 'Valid') -or ($null -eq $sig.SignerCertificate)) { 'UNTRUSTED'; exit }
  # Comparação exata por RDN: um regex sem âncora aceitaria "The Anti-Cloud Corporation Ltd".
  $parts = $sig.SignerCertificate.SubjectName.Format($true) -split [Environment]::NewLine
  $cn = ($parts | Where-Object { $_ -like 'CN=*' } | Select-Object -First 1) -replace '^CN=', ''
  $o = ($parts | Where-Object { $_ -like 'O=*' } | Select-Object -First 1) -replace '^O=', ''
  if (($cn -cne ${quote(WIRESOCK_SIGNER_SUBJECT.cn)}) -or ($o -cne ${quote(WIRESOCK_SIGNER_SUBJECT.o)})) { 'UNTRUSTED'; exit }
  # O arquivo em si também: um ACE explícito no .exe permite trocá-lo depois da checagem.
  if (-not (Test-BrisaAcl $p)) { 'UNTRUSTED'; exit }
}
$dir = [IO.Path]::GetDirectoryName(${quote(executable)})
while ($dir) {
  # A raiz do volume aceita criação de pastas por Usuários autenticados; isso não permite
  # trocar o binário, então a caminhada para antes dela.
  if ([IO.Path]::GetPathRoot($dir) -eq $dir) { break }
  if (-not (Test-BrisaAcl $dir)) { 'UNTRUSTED'; exit }
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
