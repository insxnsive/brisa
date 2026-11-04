/*
 * Vencord, a Discord client mod
 * Copyright (c) 2026 Vendicated and contributors
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

/*
 * Contratos e regras sem efeitos colaterais do transporte VPN do plugin.
 *
 * Este arquivo deliberadamente não importa Electron, Vencord ou Node. Além de
 * deixar a parte mais sensível testável, isso evita que o estado do plugin seja
 * confundido com o estado da GUI ou do standalone.
 */

export const VPN_SCHEMA_VERSION = 1;
export const VPN_OWNER_KIND = "golivebypass-plugin-vpn";
export const VPN_SERVICE_NAMES = ["wiresock-client-service", "wiresock-pro-client-service"] as const;

export type VpnMode = "proton" | "custom";
export type VpnPlatform = "windows" | "linux" | "unsupported";
/**
 * Onde a sessão Proton fica: `safe-storage` (armazenamento seguro do sistema),
 * `file` (arquivo privado, como no Windows) ou `memory-only` (nada em disco: a
 * sessão vale só enquanto o Discord estiver aberto).
 */
export type ProtonSessionStorage = "safe-storage" | "file" | "memory-only";
export type VpnState =
    | "inactive"
    | "authorizing"
    | "preparing"
    | "starting"
    | "restart_pending"
    | "active"
    | "stopping"
    | "blocked_external"
    | "dependency_missing"
    | "recovery_required";

export function normalizeProtonUsername(value: string): string {
    return value.trim().replace(/@(protonmail\.com|proton\.me|pm\.me)$/i, "");
}

export function protonUsernamesMatch(expected: string, actual: string): boolean {
    return normalizeProtonUsername(expected).toLowerCase() === normalizeProtonUsername(actual).toLowerCase();
}

export interface VpnSettings {
    mode: VpnMode;
    customConfigPath: string;
    protonUsername: string;
    protonCountry: string;
    protonFreeOnly: boolean;
    protonAutoPing: boolean;
}

export interface VpnOwnerRecord {
    kind: typeof VPN_OWNER_KIND;
    pid: number;
    generation: number;
    profilePath: string;
    configPath: string;
    probePath?: string;
    namespace?: string;
    interfaceName?: string;
    restarting?: boolean;
    createdAt: number;
}

export interface VpnDiagnostic {
    at: string;
    kind: "wireguard" | "network" | "route" | "ownership" | "dependency";
    ok: boolean;
    detail: string;
}

/** Como o perfil ativo foi escolhido — o painel usa isto para não chamar de "manual" o que veio do botão. */
export type VpnRouteSource = "auto" | "manual" | "custom" | "imported";

/**
 * Identidade da rota que o perfil ativo usa. Existe para o painel conseguir mostrar
 * "ativa em X" mesmo quando o catálogo Proton não está carregado nesta sessão (o
 * catálogo depende da sessão/medição; o perfil ativo, não).
 */
export interface VpnRouteInfo {
    mode: VpnMode;
    /** Servidor Proton quando conhecido (ex.: "NL#2"); null em modo custom ou perfil anterior a este registro. */
    server: string | null;
    /** Endpoint WireGuard lido do próprio perfil ativo (host:porta). */
    endpoint: string | null;
    /** Quando este perfil foi aplicado (epoch ms); null quando só o registro antigo existe. */
    appliedAt: number | null;
    source: VpnRouteSource | null;
}

export interface VpnStatus {
    state: VpnState;
    platform: VpnPlatform;
    architecture: string;
    owned: boolean;
    active: boolean;
    generation: number;
    discordPid: number | null;
    profilePath: string | null;
    configPath: string | null;
    namespace?: string | null;
    interfaceName?: string | null;
    requiresRelaunch?: boolean;
    dependencies?: string[];
    /** Conflito Windows comprovadamente do GoLiveBypass; ativação explícita pode retomá-lo. */
    managedConflict?: boolean;
    externalReason: string | null;
    lastDiagnostic: VpnDiagnostic | null;
    message: string;
    sessionStorage?: ProtonSessionStorage;
    /** Rota do perfil ativo; null quando não há perfil gravado. */
    route?: VpnRouteInfo | null;
}

export type VpnOperationCode =
    | "AUTHORIZATION_CANCELLED"
    | "AUTHORIZATION_FAILED"
    | "AUTHORIZATION_TIMEOUT";

export interface VpnOperationResult {
    success: boolean;
    state: VpnState;
    code?: VpnOperationCode;
    message?: string;
    error?: string;
    /** Ativação automática do boot recusada de propósito: não é falha para o usuário. */
    suppressed?: boolean;
}

export interface WireGuardConfigValidation {
    valid: boolean;
    error?: string;
    /** Código estável e de propriedade do Brisa; nunca texto do arquivo do usuário. */
    code?: string;
    /** Nome canônico de uma chave recusada, de uma tabela fixa. */
    key?: string;
}

export const DEFAULT_VPN_SETTINGS: Readonly<VpnSettings> = Object.freeze({
    mode: "proton",
    customConfigPath: "",
    protonUsername: "",
    protonCountry: "",
    protonFreeOnly: true,
    protonAutoPing: true,
});

export function normalizeVpnSettings(raw: unknown): VpnSettings {
    const value = raw !== null && typeof raw === "object" ? raw as Record<string, unknown> : {};
    const mode = value.mode === "custom" ? "custom" : "proton";
    const text = (key: string, max: number) => {
        const candidate = typeof value[key] === "string" ? value[key].trim() : "";
        return candidate.slice(0, max);
    };
    const country = text("protonCountry", 128)
        .split(",")
        .map(part => part.trim().toUpperCase())
        .filter(part => /^[A-Z]{2}$/.test(part))
        .join(",");

    return {
        mode,
        customConfigPath: text("customConfigPath", 2048),
        protonUsername: text("protonUsername", 320),
        protonCountry: country,
        protonFreeOnly: value.protonFreeOnly !== false,
        protonAutoPing: value.protonAutoPing !== false,
    };
}

export function formatAllowedApps(paths: string[]): string {
    const unique = new Map<string, string>();
    for (const raw of paths) {
        const value = raw.trim();
        if (!value) continue;
        if (/[\r\n,]/.test(value)) {
            throw new Error(`Caminho incompatível com AllowedApps: ${value.replace(/[\r\n]/g, " ")}`);
        }
        const key = value.toLowerCase();
        if (!unique.has(key)) unique.set(key, value);
    }
    if (unique.size === 0) throw new Error("Nenhum executável do Discord foi encontrado para o filtro WireSock.");
    return [...unique.values()].join(", ");
}

const HOOK_SPELLINGS: Record<string, string> = Object.freeze({
    preup: "PreUp", postup: "PostUp", predown: "PreDown", postdown: "PostDown",
    saveconfig: "SaveConfig", table: "Table",
});
const SECTION_KEYS: Record<string, readonly string[]> = Object.freeze({
    interface: ["privatekey", "address", "dns", "mtu", "listenport"],
    peer: ["publickey", "presharedkey", "allowedips", "endpoint", "persistentkeepalive"],
});
const BRISA_ALLOWED_APPS = /^#\s*@\s*ws\s*:\s*allowedapps\s*=/i;
const ANY_DIRECTIVE = /^#\s*@/;
const CONTROL_CHARS = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F\u0085\u2028\u2029]/;
const UNSAFE_VALUE = /[#;"'`$|&<>]/;
const CIDR_VALUE = /^[0-9A-Fa-f:.]+\/\d{1,3}$/;
const ENDPOINT_VALUE = /^(?:[^\s:[\]]+|\[[^\]]+\]):(\d{1,5})$/;

type ParsedConfig = { ok: true; iface: Record<string, string>; peer: Record<string, string> } |
    { ok: false; code: string; key?: string; error?: string };

function valueIsValid(key: string, value: string): boolean {
    const items = () => value.split(",").map(item => item.trim()).filter(Boolean);
    switch (key) {
        case "privatekey":
        case "publickey":
        case "presharedkey":
            return isValidWireGuardKey(value);
        case "address":
        case "allowedips": {
            const list = items();
            return list.length > 0 && list.every(item => CIDR_VALUE.test(item));
        }
        case "dns": {
            // Stripped on output; accepted as IPs or hostnames so a normal import is not refused.
            const list = items();
            return list.length > 0 && list.every(item => /^[A-Za-z0-9.:_-]+$/.test(item));
        }
        case "mtu":
            return /^\d{1,5}$/.test(value) && Number(value) >= 1 && Number(value) <= 65535;
        case "listenport":
            return /^\d{1,5}$/.test(value) && Number(value) >= 1 && Number(value) <= 65535;
        case "persistentkeepalive":
            return value.toLowerCase() === "off" || (/^\d{1,5}$/.test(value) && Number(value) <= 65535);
        case "endpoint": {
            const match = ENDPOINT_VALUE.exec(value);
            if (!match) return false;
            const port = Number(match[1]);
            if (port < 1 || port > 65535) return false;
            const host = value.slice(0, value.lastIndexOf(":"));
            return host.startsWith("[") || /^[A-Za-z0-9.-]+$/.test(host);
        }
        default:
            return false;
    }
}

/**
 * One parser for both validation and sanitization, so the file Brisa validates is the file
 * Brisa writes. Anything the elevated WireSock client could act on but Brisa does not
 * understand is rejected instead of passed through.
 */
function parseWireGuardConfig(raw: string): ParsedConfig {
    if (typeof raw !== "string" || !raw.trim()) return { ok: false, code: "PROFILE_EMPTY", error: "A configuração WireGuard está vazia." };
    // A bare CR or a Unicode line separator is a line break for some parsers and not for
    // this one; reject the file instead of letting the two disagree about what it says.
    if (CONTROL_CHARS.test(raw) || /\r(?!\n)/.test(raw)) return { ok: false, code: "PROFILE_CONTROL_CHARS" };
    const sections: Record<string, Record<string, string>> = {};
    let current: string | null = null;
    for (const rawLine of raw.split(/\r?\n/)) {
        const line = rawLine.replace(/^\uFEFF/, "").trim();
        if (!line) continue;
        if (line.startsWith("#")) {
            if (BRISA_ALLOWED_APPS.test(line)) continue;
            if (ANY_DIRECTIVE.test(line)) return { ok: false, code: "PROFILE_DIRECTIVE" };
            continue;
        }
        const header = /^\[([^\]]*)\]$/.exec(line);
        if (header) {
            const name = header[1].trim().toLowerCase();
            if (!Object.hasOwn(SECTION_KEYS, name)) return { ok: false, code: "PROFILE_UNKNOWN_SECTION" };
            if (sections[name]) return { ok: false, code: "PROFILE_DUPLICATE_SECTION" };
            sections[name] = {};
            current = name;
            continue;
        }
        const separator = line.indexOf("=");
        if (separator < 0) return { ok: false, code: "PROFILE_UNKNOWN_KEY" };
        const key = line.slice(0, separator).trim().toLowerCase();
        const value = line.slice(separator + 1).trim();
        // Brisa owns the app filter: the line is replaced, never merged, wherever it appears.
        if (key === "allowedapps") continue;
        if (!current) return { ok: false, code: "PROFILE_UNKNOWN_KEY" };
        if (Object.hasOwn(HOOK_SPELLINGS, key)) return { ok: false, code: "PROFILE_HOOKS", key: HOOK_SPELLINGS[key] };
        if (!SECTION_KEYS[current].includes(key)) return { ok: false, code: "PROFILE_UNKNOWN_KEY" };
        if (Object.hasOwn(sections[current], key)) return { ok: false, code: "PROFILE_DUPLICATE_KEY" };
        if (UNSAFE_VALUE.test(value) || !valueIsValid(key, value)) return { ok: false, code: "PROFILE_BAD_VALUE" };
        sections[current][key] = value;
    }
    if (!sections.interface) return { ok: false, code: "PROFILE_UNKNOWN_SECTION", error: "A configuração não contém a seção [Interface]." };
    if (!sections.peer) return { ok: false, code: "PROFILE_UNKNOWN_SECTION", error: "A configuração não contém a seção [Peer]." };
    return { ok: true, iface: sections.interface, peer: sections.peer };
}

export function sanitizeWireGuardConfig(raw: string, allowedApps: string): string {
    if (!raw.trim()) throw new Error("A configuração WireGuard está vazia.");
    if (!allowedApps.trim()) throw new Error("AllowedApps não pode ficar vazio.");
    if (/[\r\n\0]/.test(allowedApps)) throw new Error("AllowedApps não pode conter quebras de linha.");
    const parsed = parseWireGuardConfig(raw);
    if (!parsed.ok) throw new Error(parsed.code);
    // Rebuild instead of filtering lines: only Brisa's own keys and its single app filter
    // reach the elevated client, and DNS is dropped here rather than by the client.
    const lines = ["[Interface]",
        `PrivateKey = ${parsed.iface.privatekey}`,
        `Address = ${parsed.iface.address}`];
    if (parsed.iface.mtu) lines.push(`MTU = ${parsed.iface.mtu}`);
    if (parsed.iface.listenport) lines.push(`ListenPort = ${parsed.iface.listenport}`);
    lines.push("", "[Peer]", `PublicKey = ${parsed.peer.publickey}`);
    if (parsed.peer.presharedkey) lines.push(`PresharedKey = ${parsed.peer.presharedkey}`);
    lines.push(`AllowedIPs = ${parsed.peer.allowedips}`, `Endpoint = ${parsed.peer.endpoint}`);
    if (parsed.peer.persistentkeepalive) lines.push(`PersistentKeepalive = ${parsed.peer.persistentkeepalive}`);
    lines.push(`#@ws:AllowedApps = ${allowedApps}`);
    return `${lines.join("\r\n")}\r\n`;
}

export function isValidWireGuardKey(value: string): boolean {
    const key = value.trim();
    return /^[A-Za-z0-9+/]{43}=$/.test(key);
}

export function validateWireGuardConfig(raw: string): WireGuardConfigValidation {
    const parsed = parseWireGuardConfig(raw);
    if (!parsed.ok) return {
        valid: false, code: parsed.code,
        error: parsed.error ?? "A configuração WireGuard não é aceita.",
        ...(parsed.key ? { key: parsed.key } : {}),
    };
    const { iface, peer } = parsed;
    if (!iface.privatekey) return { valid: false, error: "A configuração não contém PrivateKey." };
    if (!isValidWireGuardKey(iface.privatekey)) return { valid: false, error: "PrivateKey inválida: esperada uma chave WireGuard Base64 de 32 bytes." };
    if (!iface.address) return { valid: false, error: "A configuração não contém Address." };
    if (!peer.publickey) return { valid: false, error: "A configuração não contém PublicKey." };
    if (!isValidWireGuardKey(peer.publickey)) return { valid: false, error: "PublicKey inválida: esperada uma chave WireGuard Base64 de 32 bytes." };
    const allowedIps = (peer.allowedips ?? "").split(",").map(value => value.trim());
    if (!allowedIps.includes("0.0.0.0/0") && !allowedIps.includes("::/0"))
        return { valid: false, error: "A configuração precisa anunciar uma rota padrão em AllowedIPs." };
    if (!peer.endpoint) return { valid: false, error: "A configuração não contém um Endpoint válido." };
    return { valid: true };
}

export function isSupportedWindowsArchitecture(platform: string, arch: string): boolean {
    return platform === "win32" && arch === "x64";
}

export function isSupportedLinuxArchitecture(platform: string, arch: string): boolean {
    return platform === "linux" && arch === "x64";
}

export function isSupportedVpnArchitecture(platform: string, arch: string): boolean {
    return isSupportedWindowsArchitecture(platform, arch) || isSupportedLinuxArchitecture(platform, arch);
}

export function safeDiagnosticDetail(value: unknown, max = 300): string {
    return String(value instanceof Error ? value.message : value ?? "")
        .replace(/[\r\n\t]+/g, " ")
        .replace(/(PrivateKey\s*=\s*)\S+/gi, "$1<redacted>")
        .replace(/(password|token|secret|authorization)\s*[:=]\s*\S+/gi, "$1=<redacted>")
        .slice(0, max);
}

/** Lê `Endpoint = host:porta` do perfil WireGuard, sem interpretar o resto do arquivo. */
export function readWireGuardEndpoint(raw: string): string | null {
    const parsed = parseWireGuardConfig(raw);
    const endpoint = parsed.ok ? parsed.peer.endpoint ?? "" : "";
    if (!endpoint) return null;
    const cleaned = endpoint.replace(/[^\x20-\x7E]/g, "").trim().slice(0, 120);
    return cleaned || null;
}

/**
 * Texto curto da rota para a UI. Não promete localização geográfica: mostra o servidor
 * quando ele foi registrado e sempre o endpoint que está no perfil ativo.
 */
export function formatVpnRouteSummary(route: VpnRouteInfo | null | undefined): string | null {
    if (!route) return null;
    const parts: string[] = [];
    if (route.server) parts.push(route.server);
    else parts.push(route.mode === "custom" ? "arquivo .conf personalizado" : "perfil Proton");
    if (route.endpoint) parts.push(route.endpoint);
    return parts.join(" · ");
}

/**
 * Rótulo de "Estado da rota" a partir do estado real do túnel — antes ele vinha só do
 * fluxo de otimização e dizia "pronta para otimizar" com o túnel já ativo.
 */
export function vpnRouteStateLabel(input: {
    status: { state?: VpnState | string | null; active?: boolean } | null | undefined;
    optimizing: boolean;
    optimizationNotice: string;
}): string {
    if (input.optimizing) return "otimizando a rota automaticamente";
    const status = input.status;
    if (!status) return input.optimizationNotice;
    if (status.active || status.state === "active") return "ativa";
    switch (status.state) {
        case "authorizing": return "aguardando autorização";
        case "preparing": return "preparando o túnel";
        case "starting": return "iniciando o túnel";
        case "restart_pending": return "reinicie o Discord para concluir";
        case "stopping": return "desativando";
        case "blocked_external": return "bloqueada por outro WireSock ativo";
        case "dependency_missing": return "dependências do sistema ausentes";
        case "recovery_required": return "recuperação necessária";
        default: return input.optimizationNotice;
    }
}
