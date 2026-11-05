// Turns the elevated start script's result file into one stable reason code. Pure and
// import-free so it can be tested without Windows, a driver or a real tunnel.
//
// The result file is untrusted: it carries vendor prose and paths from the elevated
// worker. Only fixed codes and numbers leave this module; the detail text never does.

export class WireSockStartError extends Error {
    constructor(code, data = {}) {
        super(code);
        this.name = "WireSockStartError";
        this.code = code;
        this.data = data;
    }
}

// Byte-for-byte the success path the native start already relied on.
const SUCCESS = /^0\nDIRECT_RUNNING: pid=\d+\s*$/;

export function classifyStartResult(resultText, execFailure = null) {
    const text = typeof resultText === "string" ? resultText.replace(/\r\n/g, "\n") : "";
    if (SUCCESS.test(text)) return { ok: true };
    if (!text.trim()) {
        const data = {};
        if (execFailure && Number.isFinite(execFailure.exitCode)) data.exitCode = execFailure.exitCode;
        if (execFailure && typeof execFailure.timedOut === "boolean") data.timedOut = execFailure.timedOut;
        // UAC declined, the elevated worker never reported, or the wrapper timed out: the
        // three cannot be told apart from here, so they share one code and one message.
        return { ok: false, code: "START_NOT_APPROVED", data };
    }
    const parsed = /^(\d+)\n([\s\S]*)$/.exec(text);
    if (!parsed) return { ok: false, code: "START_REFUSED_UNKNOWN", data: {} };
    const detail = parsed[2];
    if (detail.includes("EXTERNAL_SERVICE_ACTIVE")) return { ok: false, code: "START_EXTERNAL_SERVICE", data: {} };
    if (detail.includes("EXTERNAL_PROCESS_ACTIVE")) return { ok: false, code: "START_EXTERNAL_PROCESS", data: {} };
    if (detail.includes("CANCELLED")) return { ok: false, code: "START_CANCELLED", data: {} };
    const exited = /DIRECT_EXITED:\s*(-?\d{1,10})/.exec(detail);
    if (exited) return { ok: false, code: "START_CLIENT_EXITED", data: { exitCode: Number(exited[1]) } };
    return { ok: false, code: "START_REFUSED_UNKNOWN", data: {} };
}
