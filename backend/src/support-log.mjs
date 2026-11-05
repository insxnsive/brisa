// O log de suporte é montado aqui e em nenhum outro lugar. Cada campo passa por uma lista de
// permissão com forma e procedência: a forma sozinha não prova origem, então um valor de texto só
// entra se for um código que o próprio app define, nunca uma string que por acaso veio de fora
// (usuário, Discord, WireSock ou arquivo).

const HEADER = "Contains no account names, passwords, file paths or IP addresses.";

// Códigos que o app conhece. Qualquer outro texto é descartado inteiro, sem cortar.
const CODES = Object.freeze({
    discordFlavour: new Set(["STABLE", "PTB", "CANARY", "OTHER"]),
    trustVerdict: new Set(["TRUSTED", "UNTRUSTED"]),
    mode: new Set(["PROTON", "CUSTOM", "NONE"]),
    protocol: new Set(["WIREGUARD_UDP"]),
    helper: new Set(["available", "missing"]),
    phase: new Set(["DISCONNECTED", "CONNECTING", "CONNECTED", "DISCONNECTING", "LIMITED", "BLOCKED", "ERROR"]),
});

const SLUG = /^[a-z][a-z-]{1,31}$/;
// O veredito da rota é um código do app em minúsculas com sublinhado, e está na lista explícita:
// estes são os únicos valores que route-verification.mjs produz.
const ROUTE_CHECKS = new Set(["not_checked", "probe_failed", "discord_failed", "same_as_direct", "verified"]);
const ISO_COUNTRY = /^[A-Z]{2}$/;
const EVENT_LINE = /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z [a-z][a-z-]{1,31} [A-Z][A-Z0-9_]{2,47}( [A-Za-z][A-Za-z0-9]{0,23}=[A-Za-z0-9_.-]{1,47}){0,6}$/;

const code = (group, value) => CODES[group]?.has(value) ? value : null;
const integer = value => Number.isInteger(value) && value >= 0 && value <= 9999 ? value : null;
const flag = value => typeof value === "boolean" ? value : null;

/// Monta o texto final. `facts` são fatos do app; `events` são linhas já redigidas pelo
/// módulo de diagnósticos no momento da escrita.
export function renderSupportLog({ generatedAt, facts = {}, events = [] }) {
    const at = generatedAt instanceof Date ? generatedAt : new Date(generatedAt ?? Date.now());
    const lines = [`Brisa log. Generated ${at.toISOString()}. ${HEADER}`, ""];

    const add = (label, value) => { if (value !== null && value !== undefined) lines.push(`${label}: ${value}`); };

    add("Elevated", flag(facts.elevated));
    // O helper que vem no pacote é a primeira coisa que o suporte precisa saber: sem ele nenhum login
    // funciona. É um código do app, não prosa do fornecedor.
    add("Packaged Proton helper", code("helper", facts.helper));
    add("Discord client", code("discordFlavour", facts.discordFlavour));

    // Versão em quatro inteiros: um texto pontuado seria justamente o tipo de string que este
    // arquivo se recusa a aceitar.
    const version = ["sdkMajor", "sdkMinor", "sdkBuild", "sdkRevision"].map(key => integer(facts[key]));
    if (version.every(part => part !== null)) lines.push(`WireSock SDK version: ${version.join(".")}`);

    add("WireSock trust", code("trustVerdict", facts.trustVerdict));
    add("Mode", code("mode", facts.mode?.toUpperCase?.()));
    add("Protocol", code("protocol", facts.protocol?.toUpperCase?.()));
    add("Phase", code("phase", facts.phase?.toUpperCase?.()));
    if (typeof facts.stage === "string" && SLUG.test(facts.stage)) lines.push(`Stage: ${facts.stage}`);
    // O veredito da checagem de rota é um código do app (probe_failed, verified...), não prosa do
    // fornecedor: continua no log porque é o que a pessoa mais precisa no suporte.
    if (ROUTE_CHECKS.has(facts.routeCheck)) lines.push(`Route check: ${facts.routeCheck}`);
    // O país é a rota escolhida pelo usuário, não a localização dele.
    if (typeof facts.routeCountry === "string" && ISO_COUNTRY.test(facts.routeCountry)) lines.push(`Route country: ${facts.routeCountry}`);

    const kept = events.filter(line => typeof line === "string" && EVENT_LINE.test(line)).slice(-20);
    lines.push("", kept.length ? "Events (this session):" : "Events (this session): none recorded");
    lines.push(...kept);
    return lines.join("\n");
}
