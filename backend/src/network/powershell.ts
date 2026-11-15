/**
 * Um literal PowerShell entre aspas simples, com aspas internas duplicadas.
 * Compartilhado porque a regra de escape precisa ser idêntica em todo script que
 * monta caminhos ou argumentos: uma divergência vira injeção ou caminho quebrado.
 */
export function quotePowerShell(value: string): string {
    return `'${value.replace(/'/g, "''")}'`;
}

export const POWERSHELL_UTF8_PRELUDE = "[Console]::InputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);";

export function powerShellCommandArgs(script: string): string[] {
    return ["-NoProfile", "-NonInteractive", "-Command", `${POWERSHELL_UTF8_PRELUDE}\n${script}`];
}
