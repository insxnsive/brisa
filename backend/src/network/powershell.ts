/**
 * Um literal PowerShell entre aspas simples, com aspas internas duplicadas.
 * Compartilhado porque a regra de escape precisa ser idêntica em todo script que
 * monta caminhos ou argumentos: uma divergência vira injeção ou caminho quebrado.
 */
export function quotePowerShell(value: string): string {
    return `'${value.replace(/'/g, "''")}'`;
}
