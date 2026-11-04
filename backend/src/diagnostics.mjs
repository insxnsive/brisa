// Records reason codes and numbers only. Never paths, usernames, vendor text or error
// messages: this file is the single owner of backend diagnostics, and its whole value is
// that a support log can be read without exposing anything personal.
//
// Deliberately does not reuse network/logger.ts: that module's safeLogField redacts every
// string that is not an allowlisted key, so a reason code would be written as [redacted].

import fs from "node:fs";

const STAGE = /^[a-z][a-z-]{1,31}$/;
const CODE = /^[A-Z][A-Z0-9_]{2,47}$/;
const DATA_KEY = /^[A-Za-z][A-Za-z0-9]{0,23}$/;
const DATA_CODE = /^[A-Z][A-Z0-9_]{1,47}$/;
// Forma não é procedência: "NATE" também passaria no regex. Um valor de texto só entra se for um
// código que este app define, então nada que venha do Proton, do Discord, do WireSock ou de um
// arquivo pode atravessar essa porta por acaso.
const DATA_VALUES = new Set([
    "TRUSTED", "UNTRUSTED", "STABLE", "PTB", "CANARY", "OTHER",
    "PROTON", "CUSTOM", "NONE", "WIREGUARD_UDP",
    "starting-tunnel", "stopping-tunnel", "installing-wiresock", "idle",
]);
const MAX_DATA_KEYS = 6;

const cleanData = data => {
    const entries = [];
    if (!data || typeof data !== "object") return entries;
    for (const [key, value] of Object.entries(data)) {
        if (entries.length >= MAX_DATA_KEYS) break;
        if (!DATA_KEY.test(key)) continue;
        if (typeof value === "number" && Number.isFinite(value)) entries.push([key, String(value)]);
        else if (typeof value === "boolean") entries.push([key, String(value)]);
        else if (typeof value === "string" && DATA_CODE.test(value) && DATA_VALUES.has(value)) entries.push([key, value]);
        // Anything else (a path, free text, an object) is dropped whole, never truncated.
    }
    return entries;
};

const format = event => [`${event.at} ${event.stage} ${event.code}`,
    ...event.data.map(([key, value]) => `${key}=${value}`)].join(" ");

export function createDiagnostics({
    file = null,
    now = Date.now,
    maxEvents = 50,
    maxFileBytes = 128 * 1024,
    fs: fileSystem = fs,
} = {}) {
    const events = [];
    let knownSize = null;

    // The directory is the user's own private data directory. If an earlier elevated run
    // created the file with an ACL this user cannot append to, the append fails and is
    // skipped; the in-memory ring still works. Never try to repair ACLs from here.
    const size = () => {
        if (knownSize !== null) return knownSize;
        try { knownSize = fileSystem.statSync(file).size; } catch { knownSize = 0; }
        return knownSize;
    };

    const append = line => {
        if (!file) return;
        try {
            const bytes = Buffer.byteLength(line) + 1;
            if (size() + bytes > maxFileBytes) {
                const keep = Math.floor(maxFileBytes / 2);
                const current = fileSystem.readFileSync(file, "utf8");
                const tail = Buffer.from(current).subarray(Math.max(0, Buffer.byteLength(current) - keep)).toString("utf8");
                // Cut at a line boundary so a half-written event never survives the rotation.
                const boundary = tail.indexOf("\n");
                const kept = boundary >= 0 ? tail.slice(boundary + 1) : tail;
                fileSystem.writeFileSync(file, kept);
                knownSize = Buffer.byteLength(kept);
            }
            fileSystem.appendFileSync(file, `${line}\n`);
            knownSize = size() + bytes;
        } catch { /* diagnostics must never break the operation it is describing */ }
    };

    return {
        record(stage, code, data = {}) {
            try {
                if (typeof stage !== "string" || !STAGE.test(stage)) return;
                if (typeof code !== "string" || !CODE.test(code)) return;
                const event = { at: new Date(now()).toISOString(), stage, code, data: cleanData(data) };
                events.push(event);
                while (events.length > maxEvents) events.shift();
                append(format(event));
            } catch { /* never throw from a diagnostics call */ }
        },
        recent(limit = 20) {
            const count = Number.isFinite(limit) && limit > 0 ? Math.min(Math.floor(limit), maxEvents) : 20;
            return events.slice(-count).map(format);
        },
    };
}
