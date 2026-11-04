/*
 * Vencord, a Discord client mod
 * Copyright (c) 2026 Vendicated and contributors
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

import { execFile, execFileSync } from "child_process";
import crypto from "crypto";
import { promises as dns } from "dns";
import fs from "fs";
import https from "https";
import os from "os";
import path from "path";

import { WireSockSetupError } from "../wiresock-setup.mjs";
import { wireSockSearchRoots, wireSockCandidatePairs } from "../wiresock-discovery.mjs";
export { wireSockSearchRoots } from "../wiresock-discovery.mjs";

import {
    formatAllowedApps,
    safeDiagnosticDetail,
    sanitizeWireGuardConfig,
    validateWireGuardConfig,
    VPN_SERVICE_NAMES,
    type WireGuardConfigValidation,
} from "./vpn-types";
import { quotePowerShell } from "./powershell.ts";
import { parseWireSockSnapshot } from "./vpn-snapshot";
import { runWireSockSnapshotOutsideMainThread } from "./vpn-snapshot-worker";

export const WIRESOCK_VERSION = "3.4.8.1";
const WIRESOCK_DOWNLOAD = "https://wiresock.net/_api/download-release.php?product=wiresock-secure-connect-sdk&platform=x64&version=3.4.8.1&channel=winget";
const WIRESOCK_INSTALLER_SHA256 = "abfeebdc645de36b95fabbed00c7fdb0bf4d0c68c5518608450619c61876d33e";
const MAX_DOWNLOAD_BYTES = 512 * 1024 * 1024;

export type WireSockLogger = (level: "info" | "warn" | "error", message: string, data?: Record<string, unknown>) => void;

export interface WireSockCandidate {
    executable: string;
    booster: string;
    executableVersion: string;
    boosterVersion: string;
}

export interface WireSockInspection {
    active: boolean;
    owned: boolean;
    reliable: boolean;
    services: string[];
    processIds: number[];
    reason: string | null;
    /** Origem agregada dos recursos ativos: só faz sentido com `reliable && active`. */
    origin: WireSockOrigin;
    /** Serviços ativos cuja config aponta para o GoLiveBypass (plugin ou GUI). */
    managedServices: string[];
    /** PIDs ativos cuja config aponta para o GoLiveBypass (plugin ou GUI). */
    managedProcessIds: number[];
    /**
     * Subconjunto comprovadamente desta instância nativa (caminho exato da config), mesmo
     * quando o estado agregado é misto ou não confiável. Nunca inclui a GUI do WireSock.
     * Presença aqui não autoriza encerrar nada: só impede que o app saia achando que não
     * há túnel próprio.
     */
    ownServices: string[];
    ownProcessIds: number[];
}

/**
 * Origem agregada dos recursos WireSock ativos. `managed` cobre apenas configs
 * comprovadamente do GoLiveBypass (plugin ou GUI); `mixed` mistura gerenciado com
 * externo/desconhecido e nunca autoriza encerrar nada; `unknown` é leitura
 * inconclusiva.
 */
export type WireSockOrigin = "plugin" | "gui" | "managed" | "external" | "mixed" | "unknown";

const UNKNOWN_WIRESOCK_STATE = "Não foi possível confirmar o estado do WireSock; estado desconhecido.";

export interface WireSockCleanupResult {
    stopped: boolean;
    servicesResidual: string[];
    processResidual: number[];
    networkLockReset: boolean;
    dnsCleared: boolean;
    dnsFlushed: boolean;
    error?: string;
}

export interface WireSockStartResult {
    executable: string;
    configPath: string;
    allowedApps: string;
}

export interface WindowsNetworkDiagnostic {
    ok: boolean;
    dnsOk: boolean;
    httpsOk: boolean;
    detail: string;
}

function logError(error: unknown): string {
    const value = error as { stderr?: unknown; stdout?: unknown; message?: unknown; code?: unknown } | null;
    return safeDiagnosticDetail(value?.stderr || value?.stdout || value?.message || error, 500);
}

function isWindows(): boolean {
    return process.platform === "win32";
}

function normalizedPath(value: string): string {
    return value.trim().replace(/^"|"$/g, "").replace(/[\\/]+/g, "\\").toLowerCase();
}

function containsConfig(commandLine: string | null, configPath: string): boolean {
    if (!commandLine) return false;
    return normalizedPath(commandLine).includes(normalizedPath(configPath));
}

/**
 * Separa uma linha de comando do Windows preservando argumentos entre aspas. Os caminhos
 * gerados pelo GoLiveBypass não contêm aspas literais; por isso basta tratar `"` como
 * delimitador e espaços fora delas como separadores.
 */
function commandTokens(commandLine: string): string[] {
    const tokens: string[] = [];
    let token = "";
    let quoted = false;
    for (const character of commandLine.trim()) {
        if (character === '"') {
            quoted = !quoted;
            continue;
        }
        if (/\s/.test(character) && !quoted) {
            if (token) {
                tokens.push(token);
                token = "";
            }
            continue;
        }
        token += character;
    }
    if (token) tokens.push(token);
    return tokens;
}

/**
 * Extrai o argumento do flag `-config`/`--config` da linha de comando. Comparar por flag
 * (e não por substring da linha inteira) impede que `-allowed-apps`, caminhos de log ou
 * `wiresock-discord.conf.bak` sejam confundidos com a config aplicada.
 */
function configArgument(commandLine: string | null): string | null {
    if (!commandLine) return null;
    const tokens = commandTokens(commandLine);
    for (let index = 0; index < tokens.length - 1; index++) {
        const flag = tokens[index].toLowerCase();
        if (flag === "-config" || flag === "--config") return normalizedPath(tokens[index + 1]);
    }
    return null;
}

function serviceRunningFromSc(name: string): boolean | null {
    try {
        const output = execFileSync("sc.exe", ["query", name], {
            encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: 5000,
        });
        if (/STATE\s*:\s*\d+\s+RUNNING/i.test(output)) return true;
        if (/STATE\s*:\s*\d+\s+STOPPED/i.test(output)) return false;
        if (/STATE\s*:\s*\d+\s+(?:START_PENDING|STOP_PENDING|PAUSED|PAUSE_PENDING|CONTINUE_PENDING)/i.test(output)) return null;
        return null;
    } catch (error) {
        return Number((error as { status?: unknown })?.status) === 1060 ? false : null;
    }
}

// A inspeção completa custava ~1,6s NA THREAD PRINCIPAL do Discord (medido na VM: sete
// spawns de PowerShell, seis deles Get-CimInstance, ~220ms só para criar cada processo), e o
// watchdog repete isso a cada 15s -- e até seis vezes seguidas quando confirma uma leitura
// transitória. Com a janela do Discord chegando a não responder por mais de 1s nas amostras
// de recon4, uma única sessão do PowerShell passou a responder tudo o que a inspeção precisa
// (estado, PathName e ProcessId dos serviços do WireSock + a lista de processos próprios).
// A checagem de slot e a limpeza seguem com os helpers baratos (sc.exe).
function wireSockSnapshotScript(names: readonly string[]): string {
    const list = names.map(name => quotePowerShell(name)).join(",");
    return `$names=@(${list})
$svc=@()
foreach($n in $names){
  $s=Get-CimInstance Win32_Service -Filter "Name='$n'" -ErrorAction SilentlyContinue
  if($s){ $svc += [PSCustomObject]@{ name=$s.Name; state=$s.State; command=$s.PathName; processId=[int]$s.ProcessId } }
  else { $svc += [PSCustomObject]@{ name=$n; state='Missing'; command=$null; processId=0 } }
}
$procs=@(Get-CimInstance Win32_Process -Filter "Name='wiresock-client.exe'" -ErrorAction SilentlyContinue | ForEach-Object { [PSCustomObject]@{ pid=[int]$_.ProcessId; commandLine=$_.CommandLine } })
[PSCustomObject]@{ services=$svc; processes=$procs } | ConvertTo-Json -Compress -Depth 4`;
}

function readWireSockSnapshot(names: readonly string[]): WireSockSnapshot | null {
    const script = wireSockSnapshotScript(names);
    try {
        const output = execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
            encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: 8000,
        }).trim();
        if (!output) return null;
        return parseWireSockSnapshot(output, names);
    } catch {
        return null;
    }
}

function readWireSockSnapshotInProcessAsync(script: string, names: readonly string[]): Promise<WireSockSnapshot | null> {
    const { promise, resolve } = Promise.withResolvers<WireSockSnapshot | null>();
    try {
        execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
            encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: 8000,
        }, (error, stdout) => {
            if (error || !stdout) {
                resolve(null);
                return;
            }
            try {
                const output = String(stdout).trim();
                resolve(output ? parseWireSockSnapshot(output, names) : null);
            } catch {
                resolve(null);
            }
        });
    } catch {
        resolve(null);
    }
    return promise;
}
export function inspectWireSock(configPath?: string, guiConfigPath?: string, snapshotOverride?: WireSockSnapshot | null): WireSockInspection {
    if (!isWindows())
        return { active: false, owned: false, reliable: true, services: [], processIds: [], reason: null, origin: "unknown", managedServices: [], managedProcessIds: [] };
    const snapshot = snapshotOverride === undefined ? readWireSockSnapshot(VPN_SERVICE_NAMES) : snapshotOverride;
    if (!snapshot) {
        // Sem o snapshot a leitura já é desconhecida de qualquer forma: os processos não têm
        // fonte barata. O sc.exe (~9ms, sem PowerShell) ainda diz quais serviços estão de pé,
        // o que é tudo o que a mensagem de estado desconhecido reporta.
        return {
            // Uma leitura incompleta não prova nem presença nem ausência. Não
            // a exponha como ativa, pois isso faria o controller classificá-la
            // incorretamente como um WireSock externo.
            active: false,
            owned: false,
            reliable: false,
            services: VPN_SERVICE_NAMES.filter(name => serviceRunningFromSc(name) === true),
            processIds: [],
            reason: UNKNOWN_WIRESOCK_STATE,
            origin: "unknown",
            managedServices: [],
            managedProcessIds: [],
        };
    }
    const serviceStates = snapshot.services.map(service => ({ name: service.name, running: service.running }));
    const services = serviceStates.filter(service => service.running === true).map(service => service.name);
    const processes = snapshot.processes;
    const processIds = processes.map(process => process.pid);
    // Posse comprovada é calculada antes de qualquer retorno por leitura incompleta: uma
    // linha de comando legível e igual ao perfil do Brisa é prova positiva, mesmo quando
    // outro recurso do mesmo snapshot não respondeu. Descartá-la fazia um estado misto
    // parecer "nenhum túnel do Brisa", e o app saía com o túnel ainda de pé.
    const commandOf = (name: string) => snapshot.services.find(service => service.name === name)?.command ?? null;
    const guiPoolPrefix = guiConfigPath
        ? `${normalizedPath(path.win32.join(path.win32.dirname(guiConfigPath), "proton-route-pool"))}\\`
        : null;
    const isGuiConfig = (argument: string | null): boolean => {
        if (!argument || !guiConfigPath) return false;
        return argument === normalizedPath(guiConfigPath) || (guiPoolPrefix !== null && argument.startsWith(guiPoolPrefix));
    };
    const classify = (commandLine: string | null): "plugin" | "gui" | "external" | null => {
        const argument = configArgument(commandLine);
        if (argument !== null && configPath && argument === normalizedPath(configPath)) return "plugin";
        if (isGuiConfig(argument)) return "gui";
        if (commandLine && commandLine.trim().length > 0) return "external";
        return null;
    };
    const ownServices = configPath ? services.filter(name => classify(commandOf(name)) === "plugin") : [];
    const pluginServicePids = new Set(snapshot.services
        .filter(service => service.running === true && ownServices.includes(service.name))
        .map(service => service.processId)
        .filter((pid): pid is number => pid !== null));
    const ownProcessIds = configPath
        ? processes.filter(process => classify(process.commandLine) === "plugin" || pluginServicePids.has(process.pid)).map(process => process.pid)
        : [];
    // A lista de processos veio do mesmo snapshot: se ela não existisse, `readWireSockSnapshot`
    // teria devolvido null. Sobra saber se cada nome de serviço respondeu um estado definitivo.
    const reliable = serviceStates.every(service => service.running !== null);
    if (!reliable) {
        return {
            active: false,
            owned: false,
            reliable: false,
            services,
            processIds,
            reason: UNKNOWN_WIRESOCK_STATE,
            origin: "unknown",
            managedServices: [],
            managedProcessIds: [],
            ownServices,
            ownProcessIds,
        };
    }
    const active = services.length > 0 || processes.length > 0;
    if (!active)
        return { active: false, owned: false, reliable: true, services: [], processIds: [], reason: null, origin: "unknown", managedServices: [], managedProcessIds: [], ownServices: [], ownProcessIds: [] };
    if (!configPath)
        return { active, owned: false, reliable: true, services, processIds, reason: "WireSock já está ativo fora do perfil do Brisa.", origin: "unknown", managedServices: [], managedProcessIds: [], ownServices: [], ownProcessIds: [] };

    if (services.some(name => commandOf(name) === null)) {
        return {
            active: false,
            owned: false,
            reliable: false,
            services,
            processIds,
            reason: "Não foi possível confirmar o perfil do serviço WireSock; estado desconhecido.",
            origin: "unknown",
            managedServices: [],
            managedProcessIds: [],
            ownServices,
            ownProcessIds,
        };
    }
    // Cada recurso ativo recebe uma origem comprovada pelo argumento de config. Um processo
    // sem linha de comando só herda a origem do serviço cujo ProcessId coincide com o PID;
    // fora disso a leitura inteira é desconhecida (nada pode ser encerrado com segurança).
    // O pool de rotas da GUI vive dentro da pasta dela e também é instância gerenciada.
    const serviceOrigin = new Map<string, "plugin" | "gui" | "external">();
    for (const name of services) serviceOrigin.set(name, classify(commandOf(name)) ?? "external");
    const servicePidsOf = (origin: "plugin" | "gui") => new Set(snapshot.services
        .filter(service => service.running === true && serviceOrigin.get(service.name) === origin)
        .map(service => service.processId)
        .filter((pid): pid is number => pid !== null));
    const guiServicePids = servicePidsOf("gui");
    const processOrigin = processes.map(process => {
        const direct = classify(process.commandLine);
        if (direct) return direct;
        if (pluginServicePids.has(process.pid)) return "plugin" as const;
        if (guiServicePids.has(process.pid)) return "gui" as const;
        return null;
    });
    if (processOrigin.some(origin => origin === null)) {
        return {
            active: false,
            owned: false,
            reliable: false,
            services,
            processIds,
            reason: "Não foi possível confirmar o perfil do processo WireSock; estado desconhecido.",
            origin: "unknown",
            managedServices: [],
            managedProcessIds: [],
            ownServices,
            ownProcessIds,
        };
    }
    const ownProcesses = processes.filter((_, index) => processOrigin[index] === "plugin");
    const managedServices = services.filter(name => serviceOrigin.get(name) === "gui");
    const managedProcesses = processes.filter((_, index) => processOrigin[index] === "gui");
    const managedProcessIds = managedProcesses.map(process => process.pid);
    const externalCount = (services.length - ownServices.length - managedServices.length)
        + (processes.length - ownProcesses.length - managedProcesses.length);
    const hasManaged = managedServices.length + managedProcessIds.length > 0;
    const hasOwn = ownServices.length + ownProcesses.length > 0;
    if (hasOwn && !hasManaged && externalCount === 0)
        return { active, owned: true, reliable: true, services, processIds, reason: null, origin: "plugin", managedServices: [], managedProcessIds: [], ownServices, ownProcessIds: ownProcesses.map(process => process.pid) };

    let origin: WireSockOrigin;
    let reason: string;
    if (externalCount > 0 && (hasManaged || hasOwn)) {
        origin = "mixed";
        reason = hasOwn && !hasManaged
            ? "WireSock próprio e externo foram detectados ao mesmo tempo; a operação foi bloqueada."
            : "WireSock externo e uma instância Brisa estão ativos ao mesmo tempo; a operação foi bloqueada.";
    } else if (externalCount > 0) {
        origin = "external";
        reason = "WireSock externo já está ativo fora do Brisa.";
    } else if (hasOwn) {
        origin = "managed";
        reason = "WireSock próprio e do Brisa estão ativos ao mesmo tempo.";
    } else {
        origin = "gui";
        reason = "Outra instância do Brisa está com o WireSock ativo.";
    }
    return { active, owned: false, reliable: true, services, processIds, reason, origin, managedServices, managedProcessIds, ownServices, ownProcessIds: ownProcesses.map(process => process.pid) };
}
export async function inspectWireSockAsync(configPath?: string, guiConfigPath?: string): Promise<WireSockInspection> {
    if (!isWindows()) return inspectWireSock(configPath, guiConfigPath);
    const names = VPN_SERVICE_NAMES;
    const run = await runWireSockSnapshotOutsideMainThread(wireSockSnapshotScript(names), 8000);
    if (run.ran) {
        const snapshot = run.stdout ? parseWireSockSnapshot(run.stdout, names) : null;
        return inspectWireSock(configPath, guiConfigPath, snapshot);
    }
    return inspectWireSock(configPath, guiConfigPath, await readWireSockSnapshotInProcessAsync(wireSockSnapshotScript(names), names));
}
function versionOf(file: string): string | null {
    try {
        const script = `(Get-Item -LiteralPath ${quotePowerShell(file)}).VersionInfo.FileVersion`;
        const output = execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
            encoding: "utf8", stdio: ["ignore", "pipe", "ignore"], windowsHide: true, timeout: 5000,
        }).trim();
        return /^\d+\.\d+\.\d+\.\d+$/.test(output) ? output : null;
    } catch {
        return null;
    }
}

function compareVersions(left: string, right: string): number {
    const a = left.split(".").map(Number);
    const b = right.split(".").map(Number);
    for (let i = 0; i < 4; i++) {
        if ((a[i] ?? 0) !== (b[i] ?? 0)) return (a[i] ?? 0) > (b[i] ?? 0) ? 1 : -1;
    }
    return 0;
}

export function selectWireSockCandidate(candidates: WireSockCandidate[]): WireSockCandidate | null {
    return candidates
        .filter(candidate => compareVersions(candidate.executableVersion, WIRESOCK_VERSION) >= 0)
        .filter(candidate => compareVersions(candidate.boosterVersion, WIRESOCK_VERSION) >= 0)
        .filter(candidate => candidate.executableVersion === candidate.boosterVersion)
        .sort((a, b) => compareVersions(b.executableVersion, a.executableVersion))[0] ?? null;
}

export function findWireSockCandidate(env: NodeJS.ProcessEnv = process.env, roots: string[] = wireSockSearchRoots(env)): WireSockCandidate | null {
    if (!isWindows()) return null;
    const candidates = wireSockCandidatePairs(roots).flatMap(pair => {
        const executableVersion = versionOf(pair.executable);
        const boosterVersion = versionOf(pair.booster);
        return executableVersion && boosterVersion
            ? [{ ...pair, executableVersion, boosterVersion }]
            : [];
    });
    return selectWireSockCandidate(candidates);
}

function abortError(): Error {
    const error = new Error("The operation was aborted.");
    error.name = "AbortError";
    return error;
}

function isAbortError(error: unknown): boolean {
    return (error as { name?: string } | null)?.name === "AbortError";
}

function throwIfAborted(signal?: AbortSignal): void {
    if (signal?.aborted) throw abortError();
}

export function downloadInstaller(target: string, url = WIRESOCK_DOWNLOAD, redirects = 0, signal?: AbortSignal, http: Pick<typeof https, "get"> = https): Promise<void> {
    return new Promise((resolve, reject) => {
        if (signal?.aborted) { reject(abortError()); return; }
        try {
            const parsed = new URL(url);
            if (parsed.protocol !== "https:" || !/(^|\.)wiresock\.net$/i.test(parsed.hostname)) {
                reject(new Error("Redirecionamento para host não autorizado do instalador WireSock."));
                return;
            }
        } catch {
            reject(new Error("URL oficial do WireSock inválida."));
            return;
        }
        // Uma falha de request depois de o arquivo abrir precisa passar pelo mesmo caminho de
        // fechamento da transferência; senão o finally do setup apaga o temporário com o
        // descritor ainda aberto.
        let activeFail: ((error: Error) => void) | null = null;
        const request = http.get(url, { signal }, response => {
            if (response.statusCode && response.statusCode >= 300 && response.statusCode < 400 && response.headers.location) {
                response.resume();
                if (redirects >= 3) { reject(new Error("Muitos redirecionamentos no instalador WireSock.")); return; }
                // Um sinal abortado no meio de um redirecionamento não pode seguir para o próximo host.
                if (signal?.aborted) { reject(abortError()); return; }
                // O endpoint oficial pode apontar para o CDN do próprio wiresock.net.
                let next: string;
                try {
                    next = new URL(response.headers.location, url).toString();
                } catch {
                    // Um Location malformado não pode escapar do callback assíncrono: isso derrubaria
                    // o processo em vez de falhar o setup.
                    reject(new Error("Redirecionamento inválido do instalador WireSock."));
                    return;
                }
                void downloadInstaller(target, next, redirects + 1, signal).then(resolve, reject);
                return;
            }
            if (response.statusCode !== 200) {
                response.resume();
                reject(new Error(`Download WireSock retornou HTTP ${response.statusCode ?? "desconhecido"}.`));
                return;
            }
            const output = fs.createWriteStream(target, { flags: "wx" });
            let total = 0;
            let done = false;
            const fail = (error: Error) => {
                if (done) return;
                done = true;
                signal?.removeEventListener("abort", onAbort);
                response.destroy();
                // Rejeitar só depois do 'close': o finally do setup apaga o diretório temporário,
                // e no Windows apagar um arquivo que ainda está fechando falha silenciosamente.
                output.once("close", () => reject(error));
                output.destroy();
            };
            const onAbort = () => fail(abortError());
            activeFail = fail;
            signal?.addEventListener("abort", onAbort, { once: true });
            response.on("data", (chunk: Buffer) => {
                total += chunk.length;
                if (total > MAX_DOWNLOAD_BYTES) fail(new Error("O instalador WireSock excede o limite de tamanho."));
            });
            response.once("error", fail);
            output.once("error", fail);
            response.pipe(output);
            output.once("finish", () => output.close(error => {
                if (error) fail(error);
                else if (!done) {
                    done = true;
                    signal?.removeEventListener("abort", onAbort);
                    resolve();
                }
            }));
        });
        request.setTimeout(120_000, () => request.destroy(new Error("Timeout ao baixar o instalador WireSock.")));
        request.once("error", error => {
            // Depois do HTTP 200 existe arquivo aberto: a falha do request tem de esperar o
            // fechamento do descritor como qualquer outra falha de transferência.
            if (activeFail) { activeFail(signal?.aborted ? abortError() : error); return; }
            // O AbortSignal chega aqui como erro de request; preservar a identidade do cancelamento
            // é o que permite o chamador distinguir cancelamento de falha de setup.
            reject(signal?.aborted ? abortError() : error);
        });
    });
}

async function sha256(file: string, signal?: AbortSignal, guard?: () => void): Promise<string> {
    // Stream the installer: it can reach the 512 MiB cap, so never hold it in memory.
    const hash = crypto.createHash("sha256");
    const stream = fs.createReadStream(file, { highWaterMark: 1024 * 1024 });
    try {
        for await (const chunk of stream) {
            throwIfAborted(signal);
            guard?.();
            hash.update(chunk as Buffer);
        }
    } finally {
        stream.destroy();
    }
    return hash.digest("hex");
}

export interface ElevatedInstallerOptions {
    exec?: typeof execFile;
    /** Chamado quando o cancelamento chega depois do lançamento: o instalador visível continua. */
    onPending?: () => void;
    /** Saída real do processo do fornecedor, exatamente uma vez, inclusive no caminho cancelado. */
    onExited?: (info: { elapsedMs: number; cancelled: boolean }) => void;
    /** Caminho do registro de conclusão gravado pelo wrapper (seam de teste). */
    completionPath?: string;
}

/**
 * O código de saída do PowerShell não prova nada: o próprio catch do wrapper sai 1 e o UAC
 * recusado sai 1223 sem instalar nada. O que prova é o registro gravado depois de -Wait.
 */
export function runElevatedInstaller(installer: string, signal?: AbortSignal, options: ElevatedInstallerOptions = {}): Promise<void> {
    // Show the vendor's installer UI so users can review and accept its terms.
    // Nunca elevar uma operação já cancelada: o pedido de UAC não pode nascer depois do aborto.
    if (signal?.aborted) return Promise.reject(abortError());
    const completionPath = options.completionPath ?? path.join(os.tmpdir(), `brisa-wiresock-complete-${process.pid}-${Date.now()}.txt`);
    const command = `try { $p=Start-Process -FilePath ${quotePowerShell(installer)} -Verb RunAs -WindowStyle Normal -Wait -PassThru -ErrorAction Stop; if($null -eq $p){ exit 1223 }; Set-Content -LiteralPath ${quotePowerShell(completionPath)} -Value ("COMPLETED:" + [int]$p.ExitCode) -ErrorAction SilentlyContinue; exit [int]$p.ExitCode } catch { if($_.Exception.NativeErrorCode -eq 1223){ Set-Content -LiteralPath ${quotePowerShell(completionPath)} -Value "REFUSED" -ErrorAction SilentlyContinue; exit 1223 }; Write-Error $_; exit 1 }`;
    const exec = options.exec ?? execFile;
    const startedAt = Date.now();
    // O registro do wrapper distingue três casos que o código de saída confunde: instalador
    // concluído, elevação recusada e wrapper que morreu sem resultado. Qualquer outra coisa é
    // lixo de disco e não pode ser lida como conclusão.
    type InstallerRecord = { kind: "completed"; exitCode: number } | { kind: "refused" };
    const readRecord = (): InstallerRecord | null => {
        try {
            const value = fs.readFileSync(completionPath, "utf8").trim();
            fs.rmSync(completionPath, { force: true });
            if (value === "REFUSED") return { kind: "refused" };
            const match = /^COMPLETED:(-?\d+)$/.exec(value);
            if (!match) return null;
            return { kind: "completed", exitCode: Number(match[1]) };
        } catch {
            return null;
        }
    };
    return new Promise((resolve, reject) => {
        let settled = false;
        let cancelledWhileRunning = false;
        // Uma falha de spawn (ENOENT, EACCES) não prova que o instalador do fornecedor rodou.
        let spawned = false;
        const onAbort = () => {
            // O instalador visível já foi lançado: matar o wrapper PowerShell não encerra o
            // instalador elevado, então o cancelamento fica pendente até o processo sair.
            cancelledWhileRunning = true;
            options.onPending?.();
        };
        const settle = (error: Error | null, phase: "callback" | "error") => {
            if (settled) return;
            settled = true;
            signal?.removeEventListener("abort", onAbort);
            const record = readRecord();
            if (record === null) {
                // Depois do lançamento, perder a supervisão não prova que o instalador do
                // fornecedor terminou: nada de EXITED, de cancelamento concluído ou de limpeza.
                // Antes do lançamento (ENOENT), a falha é normal e não prende nada.
                if (spawned) { reject(new InstallerSupervisionLostError()); return; }
                reject(error ?? new Error("The WireSock installer could not be launched."));
                return;
            }
            if (record.kind === "refused") {
                // Elevação recusada é um resultado conhecido: nada de supervisão perdida.
                const refusal = new Error("The Windows elevation prompt was refused.") as Error & { code?: number };
                refusal.code = 1223;
                reject(refusal);
                return;
            }
            if (spawned) options.onExited?.({ elapsedMs: Date.now() - startedAt, cancelled: cancelledWhileRunning });
            if (cancelledWhileRunning) reject(abortError());
            else if (record.exitCode !== 0) {
                reject(Object.assign(new Error(`The WireSock installer exited with code ${record.exitCode}.`), { code: record.exitCode }));
            } else if (error) reject(error);
            else resolve();
        };
        signal?.addEventListener("abort", onAbort, { once: true });
        // Sem timeout: encerrar o wrapper não encerra o instalador elevado, e reportar timeout
        // aqui seria declarar um cancelamento que nunca aconteceu.
        const child = exec("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], { windowsHide: true }, error => settle(error ?? null, "callback"));
        child.once("spawn", () => { spawned = true; });
        child.once("error", error => settle(error, "error"));
    });
}

export class InstallerSupervisionLostError extends Error {
    constructor() {
        super("WireSock installer supervision was lost after launch.");
        this.name = "InstallerSupervisionLost";
    }
}

export interface WireSockSetupOptions {
    signal?: AbortSignal;
    /** Diagnóstico só com códigos e números: nunca caminhos, versões ou texto do fornecedor. */
    onEvent?: (code: string, data?: Record<string, number | boolean>) => void;
    // Seams de teste: a produção nunca passa estes campos.
    mkdtemp?: (prefix: string) => Promise<string>;
    download?: (target: string, signal?: AbortSignal) => Promise<void>;
    remove?: (directory: string) => Promise<void>;
    findCandidate?: () => WireSockCandidate | null;
    /** Confere o instalador baixado contra o pin oficial. */
    verify?: (file: string, signal?: AbortSignal) => Promise<boolean>;
    exec?: typeof execFile;
    /** Registro de conclusão do wrapper (seam de teste). */
    completionPath?: string;
}

let installInFlight: Promise<WireSockCandidate> | null = null;
// Supervisão perdida depois do lançamento: o slot fica preso de propósito até o app reiniciar.
let installSlotHeld = false;

/**
 * Garante um par EXE+booster compatível e devolve o candidato inteiro. O start precisa do par
 * (não só do caminho do executável) e o stop reusa o mesmo lease da sessão para não descobrir
 * o SDK de novo durante uma parada.
 */
export function ensureWireSockCandidate(log: WireSockLogger, options: WireSockSetupOptions = {}): Promise<WireSockCandidate> {
    // Com a supervisão perdida não se tenta instalar de novo: o instalador do fornecedor pode
    // ainda estar rodando, e uma segunda instalação concorrente seria pior que esperar.
    if (installSlotHeld) return Promise.reject(new InstallerSupervisionLostError());
    installInFlight ??= ensureWireSockCandidateOnce(log, options)
        .catch(error => {
            // Cancelamento não é falha de setup: manter a identidade deixa o chamador distinguir
            // "o usuário cancelou" de "o WireSock precisa ser instalado".
            if (error instanceof InstallerSupervisionLostError) throw error;
            if (isAbortError(error) || options.signal?.aborted) throw abortError();
            throw error instanceof WireSockSetupError ? error : new WireSockSetupError(false, wireSockSearchRoots());
        })
        .finally(() => { if (!installSlotHeld) installInFlight = null; });
    return installInFlight;
}

export async function ensureWireSockInstalled(log: WireSockLogger, options: WireSockSetupOptions = {}): Promise<string> {
    return (await ensureWireSockCandidate(log, options)).executable;
}

async function ensureWireSockCandidateOnce(log: WireSockLogger, options: WireSockSetupOptions = {}): Promise<WireSockCandidate> {
    const signal = options.signal;
    const emit = options.onEvent ?? (() => {});
    throwIfAborted(signal);
    if (!isWindows()) throw new Error("A VPN do Brisa nesta versão exige Windows x64.");
    if (process.arch !== "x64") throw new Error("A VPN do Brisa nesta versão exige Windows x64.");
    let searchedRoots = wireSockSearchRoots();
    const findCandidate = options.findCandidate ?? (() => findWireSockCandidate(process.env, searchedRoots));
    emit("SDK_CHECK_STARTED");
    const existing = findCandidate();
    if (existing) {
        emit("SDK_PRESENT");
        log("info", "WireSock SDK compatível encontrado", { version: existing.executableVersion });
        return existing;
    }

    const mkdtemp = options.mkdtemp ?? (prefix => fs.promises.mkdtemp(prefix));
    const download = options.download ?? ((target, abortSignal) => downloadInstaller(target, WIRESOCK_DOWNLOAD, 0, abortSignal));
    const remove = options.remove ?? (directory => fs.promises.rm(directory, { recursive: true, force: true }));
    let temporary: string | null = null;
    try {
        // mkdtemp fica dentro do try: uma rejeição durante um cancelamento também precisa ser
        // classificada como cancelamento, não como "falta instalar o WireSock".
        temporary = await mkdtemp(path.join(os.tmpdir(), "brisa-wiresock-"));
        throwIfAborted(signal);
        const installer = path.join(temporary, "wiresock-sdk.exe");
        log("info", "baixando instalador oficial do WireSock", { version: WIRESOCK_VERSION });
        const downloadStarted = Date.now();
        emit("SDK_DOWNLOAD_STARTED");
        await download(installer, signal);
        throwIfAborted(signal);
        const verify = options.verify ?? (async (file, abortSignal) => (await sha256(file, abortSignal)).toLowerCase() === WIRESOCK_INSTALLER_SHA256);
        if (!(await verify(installer, signal)))
            throw new Error("Hash do instalador WireSock não corresponde ao release oficial fixado.");
        // Só depois do pin conferido: um download corrompido nunca pode constar como verificado.
        emit("SDK_DOWNLOAD_VERIFIED", { elapsedMs: Date.now() - downloadStarted });
        throwIfAborted(signal);
        log("info", "instalando WireSock com elevação do Windows");
        const installStarted = Date.now();
        emit("SDK_INSTALL_STARTED");
        await runElevatedInstaller(installer, signal, {
            exec: options.exec,
            completionPath: options.completionPath,
            onPending: () => emit("SDK_INSTALL_CANCEL_PENDING"),
            // A saída do instalador é reportada pelo próprio waiter: no caminho cancelado o await
            // rejeita com AbortError e nada depois dele chegaria a rodar.
            onExited: info => emit("SDK_INSTALL_EXITED", { elapsedMs: Date.now() - installStarted, cancelled: info.cancelled }),
        });
        throwIfAborted(signal);
        searchedRoots = wireSockSearchRoots();
        const installed = findCandidate();
        if (!installed) throw new Error("O instalador WireSock terminou, mas não deixou uma instalação SDK compatível.");
        return installed;
    } catch (error) {
        if (error instanceof InstallerSupervisionLostError) {
            // Sem prova de saída do fornecedor: nada de limpar o temporário nem de liberar o slot,
            // e nada de reportar cancelamento concluído.
            installSlotHeld = true;
            emit("SDK_INSTALL_SUPERVISION_LOST");
            throw error;
        }
        if (isAbortError(error) || signal?.aborted) {
            emit("SDK_SETUP_CANCELLED");
            throw abortError();
        }
        const code = Number((error as { code?: unknown })?.code);
        emit("SDK_SETUP_FAILED", { exitCode: Number.isFinite(code) ? code : 0 });
        throw new WireSockSetupError(code === 1223, searchedRoots);
    } finally {
        if (temporary && !installSlotHeld) await remove(temporary).catch(() => {});
        // Um cancelamento durante a limpeza ainda não pode devolver sucesso.
        if (signal?.aborted && !installSlotHeld) throw abortError();
    }
}

const wait = (ms: number) => new Promise<void>(resolve => setTimeout(resolve, ms));
const WIRESOCK_INSPECTION_RETRIES = 6;

// ---------------------------------------------------------------------------
// Caminho de parada assíncrono (T2).
//
// O stop anterior usava execFileSync em cada passo, então a promessa devolvida por
// stopOwnedWireSock não significava nada: o laço de eventos ficava travado por até
// dezenas de segundos enquanto o sc/taskkill/PowerShell rodavam. Aqui todo comando
// passa por um executor assíncrono que só resolve depois de o filho fechar, e o
// orçamento de 35 s é do stop inteiro, não de cada passo.
// ---------------------------------------------------------------------------

const STOP_BUDGET_MS = 35_000;
const STOP_STEP_TIMEOUT_MS = {
    snapshot: 8_000,
    serviceStop: 30_000,
    taskkill: 15_000,
    networkLockReset: 30_000,
    dnsReset: 10_000,
    flushDns: 10_000,
} as const;

export interface StopCommandResult {
    ok: boolean;
    exitCode?: number;
    timedOut: boolean;
    aborted: boolean;
    stdout: string;
}

export interface StopRuntime {
    run(file: string, args: string[], timeoutMs: number, signal?: AbortSignal): Promise<StopCommandResult>;
    snapshot(names: readonly string[], timeoutMs: number, signal?: AbortSignal): Promise<WireSockSnapshot | null>;
    sleep(ms: number, signal?: AbortSignal): Promise<void>;
    now(): number;
}

export interface WireSockCandidateLease extends WireSockCandidate {
    executableDigest: string;
    boosterDigest: string;
}

async function fileIdentity(file: string): Promise<{ size: number; mtimeMs: number } | null> {
    try {
        const stat = await fs.promises.stat(file);
        return stat.isFile() ? { size: stat.size, mtimeMs: stat.mtimeMs } : null;
    } catch {
        return null;
    }
}

/** Aprende os digests do par dentro de uma janela estável: metadados e identidade iguais antes e depois. */
export async function learnCandidateLease(candidate: WireSockCandidate, signal?: AbortSignal, revalidate?: () => WireSockCandidate | null): Promise<WireSockCandidateLease | null> {
    try {
        const beforeExecutable = await fileIdentity(candidate.executable);
        const beforeBooster = await fileIdentity(candidate.booster);
        if (!beforeExecutable || !beforeBooster) return null;
        const executableDigest = await sha256(candidate.executable, signal);
        const boosterDigest = await sha256(candidate.booster, signal);
        // Os rótulos de versão vêm da seleção: se a descoberta não confirma mais o mesmo par e as
        // mesmas versões, o lease herdaria compatibilidade que não foi verificada.
        const current = (revalidate ?? (() => findWireSockCandidate()))();
        if (!current) return null;
        if (current.executable !== candidate.executable || current.booster !== candidate.booster) return null;
        if (current.executableVersion !== candidate.executableVersion || current.boosterVersion !== candidate.boosterVersion) return null;
        const afterExecutable = await fileIdentity(candidate.executable);
        const afterBooster = await fileIdentity(candidate.booster);
        if (!afterExecutable || !afterBooster) return null;
        // O par mudou durante o hash: nenhuma linha de base honesta existe para estes bytes.
        if (beforeExecutable.size !== afterExecutable.size || beforeExecutable.mtimeMs !== afterExecutable.mtimeMs) return null;
        if (beforeBooster.size !== afterBooster.size || beforeBooster.mtimeMs !== afterBooster.mtimeMs) return null;
        return { ...candidate, executableDigest, boosterDigest };
    } catch {
        return null;
    }
}

/**
 * O par continua sendo o mesmo bytes que esta sessão validou? Metadados são só um indício
 * rápido; o digest é a prova. Ausência, erro de leitura ou divergência invalidam o lease.
 */
export async function leaseIsFresh(lease: WireSockCandidateLease, signal?: AbortSignal, guard?: () => void): Promise<boolean> {
    try {
        guard?.();
        const beforeExecutable = await fileIdentity(lease.executable);
        const beforeBooster = await fileIdentity(lease.booster);
        if (!beforeExecutable || !beforeBooster) return false;
        if ((await sha256(lease.executable, signal, guard)) !== lease.executableDigest) return false;
        if ((await sha256(lease.booster, signal, guard)) !== lease.boosterDigest) return false;
        const afterExecutable = await fileIdentity(lease.executable);
        const afterBooster = await fileIdentity(lease.booster);
        if (!afterExecutable || !afterBooster) return false;
        return beforeExecutable.size === afterExecutable.size && beforeExecutable.mtimeMs === afterExecutable.mtimeMs
            && beforeBooster.size === afterBooster.size && beforeBooster.mtimeMs === afterBooster.mtimeMs;
    } catch {
        return false;
    }
}

export interface StopOwnedOptions {
    signal?: AbortSignal;
    timeoutMs?: number;
    runtime?: StopRuntime;
    /** Lease da sessão. Sem ele, ou com evidência velha, o reset opcional é pulado: o stop nunca descobre o SDK. */
    candidateLease?: WireSockCandidateLease | null;
    /** Sonda de frescor injetável: produção usa `leaseIsFresh`. */
    freshness?: (lease: WireSockCandidateLease, guard: () => void) => Promise<boolean>;
}

class StopCancelledError extends Error {
    constructor() { super("STOP_CANCELLED"); this.name = "StopCancelledError"; }
}

class StopDeadlineError extends Error {
    constructor() { super("STOP_TIMEOUT"); this.name = "StopDeadlineError"; }
}

function runCommandAsync(file: string, args: string[], timeoutMs: number, signal?: AbortSignal): Promise<StopCommandResult> {
    const { promise, resolve } = Promise.withResolvers<StopCommandResult>();
    if (signal?.aborted) {
        resolve({ ok: false, timedOut: false, aborted: true, stdout: "" });
        return promise;
    }
    let settled = false;
    const finish = (result: StopCommandResult) => { if (!settled) { settled = true; resolve(result); } };
    const abortedResult = (): StopCommandResult => ({ ok: false, timedOut: false, aborted: signal?.aborted === true, stdout: "" });
    let outcome: StopCommandResult | null = null;
    let child: ReturnType<typeof execFile>;
    try {
        child = execFile(file, args, { encoding: "utf8", windowsHide: true, timeout: timeoutMs, maxBuffer: 1 << 20, signal }, (error, stdout) => {
            const killed = (error as { killed?: boolean } | null)?.killed === true;
            const aborted = signal?.aborted === true;
            const code = (error as { code?: unknown } | null)?.code;
            outcome = {
                ok: !error,
                exitCode: typeof code === "number" ? code : error ? undefined : 0,
                // execFile mata o filho ao estourar o timeout; sem este teste o passo
                // pareceria apenas "falhou" e o motivo se perderia.
                timedOut: killed && !aborted,
                aborted,
                stdout: String(stdout ?? ""),
            };
        });
    } catch {
        finish(abortedResult());
        return promise;
    }
    // O callback acima não é o fim: em abort o Node o invoca antes de o processo e seus
    // streams fecharem, então resolver ali devolveria a limpeza enquanto o filho ainda roda.
    // A promessa só assenta no 'close'; uma falha de spawn não cria processo e pode assentar já.
    child.once("close", () => finish(outcome ?? abortedResult()));
    child.once("error", error => {
        if ((child as { pid?: number }).pid === undefined) finish(outcome ?? abortedResult());
        void error;
    });
    return promise;
}

export const defaultStopRuntime: StopRuntime = {
    run: runCommandAsync,
    async snapshot(names, timeoutMs, signal) {
        const script = wireSockSnapshotScript(names);
        const result = await runCommandAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], timeoutMs, signal);
        const output = result.stdout.trim();
        if (!result.ok || !output) return null;
        try { return parseWireSockSnapshot(output, names); } catch { return null; }
    },
    sleep(ms, signal) {
        const { promise, resolve } = Promise.withResolvers<void>();
        if (signal?.aborted) { resolve(); return promise; }
        const timer = setTimeout(() => { signal?.removeEventListener("abort", onAbort); resolve(); }, ms);
        const onAbort = () => { clearTimeout(timer); resolve(); };
        signal?.addEventListener("abort", onAbort, { once: true });
        return promise;
    },
    now: () => Date.now(),
};

class StopScope {
    private readonly deadlineAt: number;
    constructor(readonly runtime: StopRuntime, budgetMs: number, readonly signal?: AbortSignal) {
        this.deadlineAt = runtime.now() + budgetMs;
    }
    get aborted(): boolean { return this.signal?.aborted === true; }
    get remaining(): number { return Math.max(0, this.deadlineAt - this.runtime.now()); }
    get live(): boolean { return !this.aborted && this.remaining > 0; }
    budget(stepMs: number): number { return Math.min(stepMs, this.remaining); }
    check(): void {
        if (this.aborted) throw new StopCancelledError();
        if (this.remaining <= 0) throw new StopDeadlineError();
    }
}

/** Limita trabalho que pode travar em I/O (stat, hash) ao que resta do orçamento. */
async function withinDeadline<T>(work: Promise<T>, remainingMs: number): Promise<T> {
    if (remainingMs <= 0) throw new StopDeadlineError();
    void work.catch(() => {});
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
        return await Promise.race([
            work,
            new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new StopDeadlineError()), remainingMs); }),
        ]);
    } finally {
        if (timer) clearTimeout(timer);
    }
}

export async function stopOwnedWireSock(configPath: string, log: WireSockLogger, options: StopOwnedOptions = {}): Promise<WireSockCleanupResult> {
    if (!isWindows()) return { stopped: true, servicesResidual: [], processResidual: [], networkLockReset: false, dnsCleared: false, dnsFlushed: false };
    const runtime = options.runtime ?? defaultStopRuntime;
    const scope = new StopScope(runtime, options.timeoutMs ?? STOP_BUDGET_MS, options.signal);
    let last: WireSockInspection | null = null;
    let lastSnapshot: WireSockSnapshot | null = null;

    const refuse = (error: string, inspection: WireSockInspection | null): WireSockCleanupResult => ({
        stopped: false,
        servicesResidual: inspection?.services ?? [],
        processResidual: inspection?.processIds ?? [],
        networkLockReset: false,
        dnsCleared: false,
        dnsFlushed: false,
        error,
    });

    // A classificação continua sendo a mesma de inspectWireSock, mas sempre com um snapshot
    // não nulo: o caminho de snapshot ausente cai em serviceRunningFromSc, que é síncrono.
    const inspectAsync = async (): Promise<WireSockInspection> => {
        const snapshot = await runtime.snapshot(VPN_SERVICE_NAMES, scope.budget(STOP_STEP_TIMEOUT_MS.snapshot), scope.signal);
        lastSnapshot = snapshot;
        if (!snapshot) {
            return {
                active: false,
                owned: false,
                reliable: false,
                services: [],
                processIds: [],
                reason: UNKNOWN_WIRESOCK_STATE,
                origin: "unknown",
                managedServices: [],
                managedProcessIds: [],
            };
        }
        return inspectWireSock(configPath, undefined, snapshot);
    };

    const inspectUntilReliable = async (): Promise<WireSockInspection> => {
        let inspection = await inspectAsync();
        for (let attempt = 1; !inspection.reliable && attempt < WIRESOCK_INSPECTION_RETRIES && scope.live; attempt++) {
            await runtime.sleep(500, scope.signal);
            if (!scope.live) break;
            inspection = await inspectAsync();
        }
        return inspection;
    };

    try {
        // Um sinal já abortado não deve emitir comando algum.
        scope.check();
        const initial = await inspectAsync();
        last = initial;
        // Uma leitura inicial que terminou em cancelamento/expiração não é estado desconhecido:
        // sem esta checagem o stop reportaria UNKNOWN_WIRESOCK_STATE e esconderia a causa.
        scope.check();
        if (!initial.reliable) {
            const error = initial.reason || UNKNOWN_WIRESOCK_STATE;
            log("warn", "limpeza adiada porque o estado do WireSock é desconhecido", { motivo: error });
            return refuse(error, initial);
        }
        if (initial.active && !initial.owned) {
            const error = initial.reason || "WireSock externo detectado";
            log("warn", "limpeza recusada para preservar WireSock externo", { motivo: error });
            return refuse(error, initial);
        }
        if (!initial.active) {
            scope.check();
            log("info", "WireSock próprio já estava parado; nada a encerrar");
            return { stopped: true, servicesResidual: initial.services, processResidual: initial.processIds, networkLockReset: false, dnsCleared: false, dnsFlushed: false };
        }

        for (const name of initial.services) {
            scope.check();
            // A identidade que autoriza a parada vem da inspeção que acabou de rodar: um
            // PID/nome guardado de antes não é autoridade sobre o serviço de agora.
            const command = lastSnapshot?.services.find(service => service.name === name)?.command ?? null;
            if (!containsConfig(command, configPath)) continue;
            const result = await runtime.run("sc.exe", ["stop", name], scope.budget(STOP_STEP_TIMEOUT_MS.serviceStop), scope.signal);
            if (!result.ok) log("warn", "não consegui solicitar parada do serviço WireSock próprio", { servico: name, timedOut: result.timedOut, aborted: result.aborted });
        }

        for (let attempt = 0; attempt < 2; attempt++) {
            scope.check();
            await runtime.sleep(500, scope.signal);
            scope.check();
            const current = await inspectUntilReliable();
            last = current;
            // Uma espera que termina em cancelamento/expiração não é estado desconhecido.
            scope.check();
            if (!current.reliable) {
                const error = current.reason || UNKNOWN_WIRESOCK_STATE;
                log("error", "limpeza interrompida porque o estado do WireSock ficou desconhecido", { motivo: error });
                return refuse(error, current);
            }
            if (!current.active) break;
            if (!current.owned) {
                const error = current.reason || "WireSock externo apareceu durante a limpeza";
                log("error", "limpeza interrompida ao detectar WireSock externo", { motivo: error });
                return refuse(error, current);
            }
            for (const pid of current.processIds) {
                scope.check();
                const result = await runtime.run("taskkill.exe", ["/F", "/T", "/PID", String(pid)], scope.budget(STOP_STEP_TIMEOUT_MS.taskkill), scope.signal);
                if (!result.ok) log("warn", "não consegui encerrar processo WireSock próprio", { pid, timedOut: result.timedOut, aborted: result.aborted });
            }
        }

        // Só depois de uma leitura confiável mostrar ausência de qualquer recurso WireSock
        // ativo é que as mudanças globais (lock, DNS, cache) podem rodar -- e cada uma delas
        // repete a checagem: um túnel externo que apareça no meio não pode receber um reset
        // global baseado numa observação anterior.
        let networkLockReset = false;
        let dnsCleared = false;
        let dnsFlushed = false;

        const blockedBeforeGlobalAction = async (): Promise<WireSockCleanupResult | null> => {
            const observation = await inspectUntilReliable();
            last = observation;
            // Cancelamento/expiração depois da espera nunca pode ser lido como sucesso.
            scope.check();
            if (!observation.reliable) return refuse(observation.reason || UNKNOWN_WIRESOCK_STATE, observation);
            if (observation.active) return refuse(observation.owned ? "O WireSock próprio ainda permanece ativo." : (observation.reason || "WireSock externo apareceu durante a limpeza"), observation);
            return null;
        };

        const lease = options.candidateLease ?? null;
        if (lease && scope.live) {
            let blocked = await blockedBeforeGlobalAction();
            if (blocked) return blocked;
            // A prova de frescor consome orçamento e tempo: ela precisa caber no que resta e ser
            // seguida de um novo check. Um reset com orçamento zero roda sem timeout.
            const probe = options.freshness ?? ((value, guard) => leaseIsFresh(value, scope.signal, guard));
            const fresh = await withinDeadline(probe(lease, () => scope.check()), scope.remaining);
            scope.check();
            if (fresh) {
                // Inatividade comprovada imediatamente antes do reset, não antes do hash.
                blocked = await blockedBeforeGlobalAction();
                if (blocked) return blocked;
                scope.check();
                const result = await runtime.run(lease.executable, ["reset-network-lock"], scope.budget(STOP_STEP_TIMEOUT_MS.networkLockReset), scope.signal);
                networkLockReset = result.ok;
            }
        }
        if (scope.live) {
            const blocked = await blockedBeforeGlobalAction();
            if (blocked) return blocked;
            const script = "Get-NetAdapter -IncludeHidden | Where-Object { $_.Name -match 'ProTUN|WireSock' -or $_.InterfaceDescription -match 'ProTUN|WireSock' } | ForEach-Object { Set-DnsClientServerAddress -InterfaceIndex $_.ifIndex -ResetServerAddresses -ErrorAction SilentlyContinue }";
            const result = await runtime.run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], scope.budget(STOP_STEP_TIMEOUT_MS.dnsReset), scope.signal);
            dnsCleared = result.ok;
            if (!result.ok) log("warn", "não consegui limpar DNS do adaptador WireSock", { timedOut: result.timedOut, aborted: result.aborted });
        }
        if (scope.live) {
            const blocked = await blockedBeforeGlobalAction();
            if (blocked) return blocked;
            const result = await runtime.run("ipconfig.exe", ["/flushdns"], scope.budget(STOP_STEP_TIMEOUT_MS.flushDns), scope.signal);
            dnsFlushed = result.ok;
            if (!result.ok) log("warn", "flushdns falhou", { timedOut: result.timedOut, aborted: result.aborted });
        }

        scope.check();
        const residual = await inspectUntilReliable();
        last = residual;
        // O veredito só vale se o stop inteiro ainda estiver vivo: um abort ou um orçamento
        // esgotado durante a última leitura não pode virar stopped:true.
        scope.check();
        const stopped = residual.reliable && !residual.active;
        if (!stopped && !residual.reliable) log("error", "limpeza não confirmou o estado final do WireSock", { motivo: residual.reason || UNKNOWN_WIRESOCK_STATE });
        else if (!stopped) log("error", "limpeza deixou resíduo WireSock próprio", { services: residual.services, pids: residual.processIds });
        else if (networkLockReset) log("info", "WireSock próprio, lock e processo verificados como parados");
        else log("warn", "WireSock próprio parado (serviço e processos); o reset do network-lock não foi confirmado -- exige elevação e o Brisa não habilita esse lock", { networkLockReset });
        return {
            stopped,
            servicesResidual: residual.services,
            processResidual: residual.processIds,
            networkLockReset,
            dnsCleared,
            dnsFlushed,
            ...(stopped ? {} : {
                error: !residual.reliable
                    ? residual.reason || UNKNOWN_WIRESOCK_STATE
                    : "O WireSock próprio ainda permanece ativo.",
            }),
        };
    } catch (error) {
        // Cancelamento ou estouro do orçamento do stop inteiro nunca vira stopped:true,
        // mesmo que uma leitura anterior já tivesse visto o túnel parado.
        if (error instanceof StopCancelledError || error instanceof StopDeadlineError) {
            log("warn", error instanceof StopCancelledError ? "limpeza cancelada" : "limpeza expirou o orçamento", {});
            return refuse(error.message, last);
        }
        log("error", "limpeza falhou", { erro: logError(error) });
        return refuse("STOP_STEP_FAILED", last);
    }
}

export type RouteProbeRemoval = "removed" | "missing" | "invalid" | "busy";

export interface RouteProbeCleanupResult {
    scanned: number;
    removed: number;
    protected: number;
    recent: number;
    invalid: number;
    busy: number;
}

