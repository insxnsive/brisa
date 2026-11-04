import { execFileSync } from "node:child_process";

/**
 * Impressão digital do certificado do fornecedor (The Anti-Cloud Corporation). O binário
 * escolhido é executado como administrador, então a origem importa mais que a versão.
 */
export const WIRESOCK_SIGNER_THUMBPRINT = "013371A3E52D87E59DA1C93A305732B1501FFE5A";

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
$pin = '${WIRESOCK_SIGNER_THUMBPRINT}'
foreach ($p in @(${quote(executable)}, ${quote(booster)})) {
  $sig = Get-AuthenticodeSignature -LiteralPath $p
  if (($sig.Status -ne 'Valid') -or ($null -eq $sig.SignerCertificate) -or ($sig.SignerCertificate.Thumbprint -ne $pin)) { 'UNTRUSTED'; exit }
}
$dir = [IO.Path]::GetDirectoryName(${quote(executable)})
while ($dir) {
  # A raiz do volume aceita criação de pastas por Usuários autenticados; isso não permite
  # trocar o binário, então a caminhada para antes dela.
  if ([IO.Path]::GetPathRoot($dir) -eq $dir) { break }
  $acl = Get-Acl -LiteralPath $dir
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
