import path from "node:path";
import { WireSockSetupError, wireSockSetupFailure } from "./wiresock-setup.mjs";
import { WireSockStartError } from "./wiresock-start-result.mjs";
import { DiscordCompatibilityError, discordCompatibilityFailure } from "./discord-compatibility.mjs";

const METHODS = new Set(["captcha", "ownership-email", "ownership-sms"]);
const COMMANDS = new Set(["snapshot", "login", "logout", "optimize", "connect", "disconnect", "importConfig", "cancel", "diagnostics", "progress", "waitForIdle"]);

// Helper and vendor prose is untrusted. No pattern-based redactor can reliably
// recognize a bare credential, path, username, or token URL in that prose.
const FAILURE_MESSAGES = Object.freeze({
  INVALID_CREDENTIALS: "The Proton username or password was rejected.",
  TWO_FACTOR_REQUIRED: "Enter your Proton two-factor code and try again.",
  TWO_FACTOR_INVALID: "The Proton two-factor code was rejected or expired.",
  CAPTCHA_REQUIRED: "Complete Proton's security check and try again.",
  CAPTCHA_INVALID: "The security check was rejected or expired. Open a new one and try again.",
  CAPTCHA_CANCELLED: "Operation cancelled.",
  NETWORK_ERROR: "Could not reach Proton. Check your connection and try again.",
  TIMEOUT: "Proton did not respond in time. Try again.",
  MISSING_EXECUTABLE: "The packaged Proton helper is unavailable.",
  SESSION_PERSISTENCE: "The Proton session could not be saved. Close other Brisa instances and try again.",
  CONFIGURATION_ERROR: "The WireGuard profile could not be generated. Try another route.",
  UNKNOWN: "The Proton operation failed. Try again.",
});
const safeFailure = code => FAILURE_MESSAGES[code] || "Operation failed. Try again.";

// Start failures get their own messages: one generic line for every start failure is what
// made a refused start impossible to diagnose. Codes are app-owned; nothing from the
// elevated worker's output reaches these strings.
const START_MESSAGES = Object.freeze({
  START_EXTERNAL_SERVICE: "Another app's WireSock service is running, so Brisa did not start its own tunnel. Brisa did not touch it. Stop that tunnel, then try again.",
  START_EXTERNAL_PROCESS: "Another WireSock client is running, so Brisa did not start its own tunnel. Brisa did not touch it. Close it, then try again.",
  START_EXTERNAL_ACTIVE: "Another WireSock client is running, so Brisa did not start its own tunnel. Brisa did not touch it. Close it, then try again.",
  START_CLIENT_EXITED: "WireSock started and then stopped right away. Try another route, or check that WireSock is installed correctly.",
  START_NOT_APPROVED: "Windows did not approve the administrator step, or it did not finish in time. Approve the prompt and try again.",
  START_NOT_CONFIRMED: "WireSock started, but Brisa could not confirm it is running with Brisa's own profile. Try again.",
  START_STATE_UNRELIABLE: "Brisa could not read the WireSock state reliably, so it made no changes. Run Brisa as administrator and try again.",
  START_REFUSED_UNKNOWN: "The Brisa tunnel could not be started. Check WireSock and your VPN profile, then try again.",
});
// Only used when cleanup actually succeeded, so the claim is true when it is made.
const START_CLEANED = Object.freeze({
  START_CLIENT_EXITED: "WireSock started and then stopped right away. Brisa restored the normal connection. Try another route, or check that WireSock is installed correctly.",
  START_NOT_CONFIRMED: "WireSock started, but Brisa could not confirm it is running with Brisa's own profile, so it was stopped. Try again.",
});
const startFailure = (error, cleaned) => {
  const code = Object.hasOwn(START_MESSAGES, error?.code) ? error.code : "START_REFUSED_UNKNOWN";
  const base = START_MESSAGES[code];
  const message = cleaned ? (START_CLEANED[code] ?? base)
    : `${base} Brisa could not confirm tunnel cleanup and Discord restoration. Check the connection status before retrying.`;
  return { success: false, code, message };
};

// Profile rejections carry app-owned codes only. Nothing from the user's file is echoed.
const PROFILE_MESSAGES = Object.freeze({
  PROFILE_HOOKS: "This WireGuard file contains commands (such as PreUp or PostUp). Brisa never runs those. Remove them and import the file again.",
  PROFILE_DIRECTIVE: "This WireGuard file contains WireSock directives that Brisa does not accept. Brisa sets the app filter itself. Remove them and import again.",
  PROFILE_UNKNOWN_KEY: "This WireGuard file has a setting Brisa does not support. Use a standard file with only the usual Interface and Peer settings.",
  PROFILE_UNKNOWN_SECTION: "This WireGuard file has a section Brisa does not support. Only one Interface and one Peer are accepted.",
  PROFILE_DUPLICATE_SECTION: "This WireGuard file has more than one Interface or Peer section. Brisa accepts one of each.",
  PROFILE_DUPLICATE_KEY: "This WireGuard file repeats a setting. Keep one copy of each and import again.",
  PROFILE_BAD_VALUE: "A setting in this WireGuard file has an invalid value.",
  PROFILE_CONTROL_CHARS: "This WireGuard file contains unexpected control characters.",
});
const profileFailure = (validation, saved) => {
  const code = Object.hasOwn(PROFILE_MESSAGES, validation?.code) ? validation.code : undefined;
  const base = code ? PROFILE_MESSAGES[code] : (saved ? "The saved WireGuard profile is invalid." : "The WireGuard profile is invalid.");
  return { success: false, ...(code ? { code } : {}), message: saved && code ? `${base} (Saved profile: import it again.)` : base };
};

const safeCaptchaUrl = value => {
  if (typeof value !== "string" || value.length > 8192) return undefined;
  try {
    const url = new URL(value);
    const host = url.hostname.toLowerCase();
    if (url.protocol !== "https:" || url.username || url.password || !(host === "proton.me" || host.endsWith(".proton.me"))) return undefined;
    return url.toString();
  } catch { return undefined; }
};

const safeRoute = value => {
  if (!value || typeof value !== "object") return null;
  const server = typeof value.server === "string" ? value.server.trim().slice(0, 120) : "";
  const country = typeof value.country === "string" ? value.country.trim().toUpperCase().slice(0, 2) : "";
  if ((server && !/^[A-Za-z0-9#_. -]+$/.test(server)) || (country && !/^[A-Z]{2}$/.test(country))) return null;
  if (!server && !country) return null;
  const route = { server, country };
  if (Number.isFinite(value.pingMs) && value.pingMs >= 0 && value.pingMs <= 60_000) route.pingMs = Math.round(value.pingMs);
  return route;
};

const actionResult = value => {
  const result = { success: value?.success === true };
  if (typeof value?.code === "string" && Object.hasOwn(FAILURE_MESSAGES, value.code)) result.code = value.code;
  if (!result.success) result.message = safeFailure(result.code);
  else if (typeof value?.message === "string") result.message = "Operation completed.";
  if (result.code === "CAPTCHA_REQUIRED" || result.code === "CAPTCHA_INVALID") {
    const captchaUrl = safeCaptchaUrl(value?.captchaUrl);
    if (captchaUrl) result.captchaUrl = captchaUrl;
  }
  if (result.success) {
    const route = safeRoute(value?.route || value);
    if (route) result.route = route;
  }
  return result;
};

function assertPayloadObject(payload) {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) throw new Error("Payload must be an object.");
}

function exactKeys(payload, required, optional = []) {
  const allowed = new Set([...required, ...optional]);
  if (Object.keys(payload).some(key => !allowed.has(key))) throw new Error("Payload schema is invalid.");
  if (required.some(key => !Object.hasOwn(payload, key))) throw new Error("Payload schema is invalid.");
}

function stringField(payload, key, { required = false, max = 16_384 } = {}) {
  const value = payload[key];
  if (value === undefined && !required) return undefined;
  if (typeof value !== "string" || (required && !value.length) || value.length > max || value.includes("\0")) throw new Error("Payload schema is invalid.");
  return value;
}

export function validateCommandPayload(command, payload) {
  assertPayloadObject(payload);
  if (!COMMANDS.has(command)) throw new Error("Unknown command.");
  if (["snapshot", "logout", "connect", "disconnect", "cancel", "diagnostics", "progress", "waitForIdle"].includes(command)) {
    const optional = command === "connect" ? ["humanVerificationToken", "humanVerificationMethod"] : [];
    exactKeys(payload, [], optional);
  } else if (command === "login") {
    exactKeys(payload, ["username", "password"], ["twoFactorCode", "humanVerificationToken", "humanVerificationMethod"]);
    stringField(payload, "username", { required: true, max: 320 });
    stringField(payload, "password", { required: true, max: 4096 });
    stringField(payload, "twoFactorCode", { max: 128 });
  } else if (command === "optimize") {
    exactKeys(payload, [], ["country", "humanVerificationToken", "humanVerificationMethod"]);
    const country = stringField(payload, "country", { max: 2 });
    if (country !== undefined && country !== "" && !/^[A-Za-z]{2}$/.test(country)) throw new Error("Payload schema is invalid.");
  } else if (command === "importConfig") {
    exactKeys(payload, ["path"]);
    stringField(payload, "path", { required: true, max: 2048 });
  }
  if (["login", "optimize", "connect"].includes(command)) {
    stringField(payload, "humanVerificationToken", { max: 16_384 });
    const method = stringField(payload, "humanVerificationMethod", { max: 32 });
    if (method !== undefined && !METHODS.has(method)) throw new Error("Payload schema is invalid.");
    if (payload.humanVerificationToken && !method) throw new Error("A human-verification method is required with its token.");
  }
}

export function createBackend(deps, paths) {
  let state;
  let generation = 0;
  let activeOperation = null;
  let mutationBusy = false;
  let mutationDone = Promise.resolve();
  let releaseMutation = () => {};
  let sessionApps = [];
  let routeVerification = { verified: false, reason: "not_checked" };
  let stage = "idle";
  // O instalador do WireSock pode ter ficado sem supervisão: enquanto isso não for provado,
  // nenhuma operação nativa e nenhuma saída podem ser consideradas seguras.
  let installerUnknown = false;
  let lastRouteCheck = 0;
  // A route verdict is bound to the operation that asked for it and to the tunnel it
  // observed. `generation` covers new operations; `routeEpoch` covers an explicit reset
  // and a tunnel that disappeared or was replaced without a Brisa command.
  let routeEpoch = 0;
  let routeRecheck = null;
  let observedTunnelKey = null;
  const ROUTE_STALE_AFTER_MS = 30_000;
  const ROUTE_VERDICT_MAX_AGE_MS = 90_000;
  const now = () => deps.now?.() ?? Date.now();
  // Diagnostics are optional so every existing test fixture and the fixture entry keep working.
  const note = (stage, code, data) => { try { deps.diag?.record(stage, code, data); } catch {} };
  const ownedTunnelKey = status =>
    `${(status?.processIds ?? []).slice().sort((a, b) => a - b).join(",")}|${(status?.services ?? []).slice().sort().join(",")}`;
  // Positive evidence that this process's own native tunnel exists, independent of whether
  // the aggregate state is exclusive. Never used to authorize stopping anything.
  const ownsTunnel = status => (status?.ownServices?.length ?? 0) > 0
    || (status?.ownProcessIds?.length ?? 0) > 0
    || (status?.reliable === true && status.active === true && status.owned === true);
  // Sticky memory of this session's own tunnel. One unreadable inspection (a PowerShell
  // timeout, a null snapshot) must not erase the fact that this process started a tunnel:
  // ownership is re-proven, absence is never assumed from silence.
  let sessionOwned = false;
  const hasOwned = status => {
    if (ownsTunnel(status)) { sessionOwned = true; return true; }
    if (status?.reliable === true) { sessionOwned = false; return false; }
    return sessionOwned;
  };
  const resetRoute = () => {
    routeEpoch += 1;
    routeRecheck?.controller.abort();
    routeRecheck = null;
    routeVerification = { verified: false, reason: "not_checked" };
    lastRouteCheck = 0;
    // A fresh operation must not be mistaken for a replaced tunnel by the next snapshot.
    observedTunnelKey = null;
  };
  const liveTunnelKey = async () => {
    try {
      const status = await inspect();
      return status?.reliable === true && status.active === true && status.owned === true ? ownedTunnelKey(status) : null;
    } catch { return null; }
  };
  const clearVerdict = () => { routeVerification = { verified: false, reason: "not_checked" }; lastRouteCheck = 0; };
  const checkRoute = async (signal, { tunnelKey } = {}) => {
    const mineGeneration = generation, mineEpoch = routeEpoch;
    let result;
    try { result = await deps.verifyRoute(signal); }
    catch { result = { verified: false, reason: "probe_failed" }; }
    if (mineGeneration !== generation || mineEpoch !== routeEpoch) return false;
    // Evidence is accepted only for a tunnel this process can still see, active and owned.
    // The cached observation is not enough: the tunnel can die or be replaced in between.
    const liveKey = await liveTunnelKey();
    if (mineGeneration !== generation || mineEpoch !== routeEpoch) return false;
    if (liveKey === null || (tunnelKey !== undefined && liveKey !== tunnelKey)) {
      clearVerdict();
      return false;
    }
    observedTunnelKey = liveKey;
    routeVerification = { verified: result?.verified === true,
      reason: ["verified", "probe_failed", "discord_failed", "same_as_direct", "probe_unavailable"].includes(result?.reason) ? result.reason : "probe_failed" };
    lastRouteCheck = now();
    return true;
  };
  const startRouteRecheck = tunnelKey => {
    const controller = new AbortController();
    const promise = checkRoute(controller.signal, { tunnelKey }).catch(() => false)
      .finally(() => { if (routeRecheck?.promise === promise) routeRecheck = null; });
    routeRecheck = { promise, controller };
  };

  const loadState = async () => {
    if (state) return state;
    let raw = "";
    try { raw = await deps.files.readOwned(paths.statePath); } catch {}
    try {
      const parsed = JSON.parse(raw);
      state = { mode: parsed?.mode === "custom" ? "custom" : "proton", route: safeRoute(parsed?.route) };
    } catch { state = { mode: "proton", route: null }; }
    return state;
  };

  const saveState = async (value = state) => {
    await deps.files.writeOwned(paths.statePath, JSON.stringify({ version: 1, mode: value.mode, route: value.route }));
  };

  const inspect = () => deps.inspect(paths.ownedConfigPath);
  const mutationGuard = async () => {
    const status = await inspect();
    if (!status?.reliable) {
      note("guard", "GUARD_UNRELIABLE", { ownPresent: hasOwned(status) });
      return { status, failure: { success: false, message: "WireSock state could not be inspected reliably; no changes were made." } };
    }
    if (status.active && !status.owned) {
      note("guard", "GUARD_EXTERNAL_TUNNEL", { ownPresent: hasOwned(status) });
      // Mixed ownership is not "someone else's tunnel only": Brisa's own instance may still
      // be running, and it cannot be stopped safely while the other one exists.
      return { status, failure: { success: false, message: hasOwned(status)
        ? "Brisa's tunnel is still running alongside another WireSock tunnel. Close the other tunnel, then disconnect Brisa. Brisa will stay open until its tunnel can be stopped safely."
        : "Another WireSock tunnel is active; no changes were made." } };
    }
    return { status };
  };
  // Profile-only operations (logout, optimize) never touch the live native profile, so they
  // only need proof that no native tunnel of ours is running, not a readable WireSock state.
  const profileMutationGuard = async (message) => {
    const status = await inspect();
    if (hasOwned(status)) {
      note("guard", "GUARD_OWNED_TUNNEL", { ownPresent: true });
      return { status, failure: { success: false, message } };
    }
    return { status };
  };

  const begin = () => {
    activeOperation?.controller.abort();
    generation += 1;
    const mine = generation;
    const controller = new AbortController();
    const operation = { controller, isCurrent: () => activeOperation?.generation === mine && generation === mine && !controller.signal.aborted };
    activeOperation = { generation: mine, controller };
    return operation;
  };
  const finish = operation => { if (operation.isCurrent()) activeOperation = null; };
  const cancelled = () => ({ success: false, code: "CAPTCHA_CANCELLED", message: "Operation cancelled." });

  const username = async () => {
    if (!deps.helperAvailable()) return "";
    const value = await deps.getSavedSessionUsername(paths.dataDir);
    return typeof value === "string" ? value.trim().slice(0, 320) : "";
  };

  const discordApps = async () => {
    const found = await deps.discoverDiscord();
    const valid = [...new Set((Array.isArray(found) ? found : []).filter(value => typeof value === "string" && value.length <= 2048 && !/[\r\n,]/.test(value)))];
    if (!valid.length) throw new DiscordCompatibilityError("DISCORD_UNAVAILABLE");
    return valid;
  };

  const generate = async (payload, operation) => {
    if (!deps.helperAvailable()) return { success: false, message: "The packaged Proton helper is unavailable." };
    const currentUsername = await username();
    if (!operation.isCurrent()) return cancelled();
    if (!currentUsername) return { success: false, message: "Sign in to ProtonVPN first." };
    let freeOnly = true;
    try {
      const plan = await deps.getPlan(paths.dataDir, currentUsername);
      if (!operation.isCurrent()) return cancelled();
      freeOnly = !(plan?.success === true && plan.status === "premium");
      // Recording only: a premium user whose lookup fails still falls back to free servers.
      if (plan?.success !== true) note("plan", "PLAN_UNKNOWN", { freeOnly });
    } catch { note("plan", "PLAN_LOOKUP_FAILED", { freeOnly: true }); }
    const generated = await deps.generate(paths.dataDir, {
      username: currentUsername,
      countries: payload.country ? payload.country.toUpperCase() : undefined,
      freeOnly,
      autoPing: true,
      signal: operation.controller.signal,
      humanVerificationToken: payload.humanVerificationToken,
      humanVerificationMethod: payload.humanVerificationMethod,
    });
    if (!operation.isCurrent()) return cancelled();
    const normalized = actionResult(generated);
    if (normalized.success) {
      const previousState = { mode: state.mode, route: state.route };
      const nextState = { mode: "proton", route: safeRoute(generated) };
      await saveState(nextState);
      if (!operation.isCurrent()) {
        try { await saveState(previousState); } catch {}
        return cancelled();
      }
      state = nextState;
    }
    return normalized;
  };

  const rollbackStart = async (apps) => {
    // Sinal próprio desta limpeza: o sinal do connect já está abortado quando o rollback roda,
    // e reaproveitá-lo faria o stop desistir antes de encerrar o túnel.
    const cleanupController = new AbortController();
    sessionApps = [];
    resetRoute();
    try {
      const status = await inspect();
      if (!status?.reliable || (status.active && !status.owned)) {
        note("stop", "ROLLBACK_BLOCKED");
        return false;
      }
      if (status.active) {
        await deps.stopDiscord(apps, undefined, { cleanup: true });
        // A limpeza do rollback tem sinal próprio: o sinal do connect já está abortado quando
        // chegamos aqui, e reaproveitá-lo faria o stop desistir antes de encerrar o túnel.
        const stopped = await deps.stop(paths.ownedConfigPath, { signal: cleanupController.signal });
        if (stopped?.stopped === true) sessionOwned = false;
        note("stop", stopped?.stopped === true ? "STOP_CONFIRMED" : "STOP_NOT_CONFIRMED", {
          rollback: true,
          services: stopped?.servicesResidual?.length ?? 0,
          processes: stopped?.processResidual?.length ?? 0,
          networkLockReset: stopped?.networkLockReset === true,
        });
        if (stopped?.stopped !== true) return false;
      }
      const restored = await inspect();
      if (!restored.reliable || restored.active) return false;
      // Rollback must restore the application as well as its normal route.
      await deps.launchDiscord(apps);
      return true;
    } catch { return false; }
  };
  const cleanupCancelledStart = async (apps) => (await rollbackStart(apps)) ? cancelled() :
    { success: false, message: "Cancellation could not confirm normal-route restoration. Check tunnel status and Discord before retrying." };
  const cancelledStart = (apps) => {
    note(stage === "idle" ? "startup" : stage, "START_CANCELLED");
    return cleanupCancelledStart(apps);
  };

  return {
    async execute(command, payload) {
      validateCommandPayload(command, payload);
      const enteredGeneration = generation;
      if (command === "cancel") {
        generation += 1;
        activeOperation?.controller.abort();
        activeOperation = null;
        // Abort the background probes too; the generation bump already discards their
        // result, this stops the processes instead of leaving them to time out.
        routeRecheck?.controller.abort();
        routeRecheck = null;
        return { success: true };
      }
      if (command === "waitForIdle") {
        if (installerUnknown) return { success: false, code: "SDK_INSTALL_SUPERVISION_LOST", message: "WireSock installation status is unknown. Restart Brisa before trying again." };
        await mutationDone;
        // A espera pode ter começado antes da perda de supervisão: sem esta segunda checagem
        // um chamador já pendente voltaria com sucesso.
        if (installerUnknown) return { success: false, code: "SDK_INSTALL_SUPERVISION_LOST", message: "WireSock installation status is unknown. Restart Brisa before trying again." };
        return { success: true };
      }
      if (command === "progress") return { success: true, stage };
      await loadState();
      if (["login", "optimize", "connect"].includes(command) && generation !== enteredGeneration) return cancelled();

      if (command === "snapshot") {
        const mine = generation;
        const status = await inspect();
        const tunnelActive = status?.reliable === true && status.active === true && status.owned === true;
        // Uma inspeção antiga não pode reescrever a memória de posse da sessão: só a geração
        // atual e um estado sem mutação podem dizer que não há túnel próprio.
        const fresh = mine === generation && !mutationBusy;
        const ownPresent = fresh ? hasOwned(status) : (ownsTunnel(status) || sessionOwned);
        const tunnelKey = tunnelActive ? ownedTunnelKey(status) : null;
        // Publish inspection-derived state before any unrelated await, and only while this
        // snapshot is still current, so a late snapshot cannot overwrite a newer tunnel.
        if (mine === generation && !mutationBusy) {
          if (!tunnelActive) {
            resetRoute();
            // A positively identified native tunnel (mixed or partly readable) may still be
            // running, so keep the Discord session list for a later safe disconnect. Only a
            // reliable observation of no native tunnel at all clears it.
            if (!ownPresent && status?.reliable === true) sessionApps = [];
          }
          // A different owned tunnel in the same generation invalidates the old verdict.
          else if (observedTunnelKey !== null && observedTunnelKey !== tunnelKey) resetRoute();
          observedTunnelKey = tunnelKey;
        }
        const currentUsername = await username();
        let discordRunning = false;
        if (tunnelActive && sessionApps.length && !mutationBusy) {
          try { discordRunning = await deps.discordRunning(sessionApps); } catch {}
          const age = lastRouteCheck ? now() - lastRouteCheck : Infinity;
          // Never block a snapshot (or ExitGuard) on the route probes; the verdict lands
          // in a later snapshot once the re-check completes.
          if (discordRunning && age >= ROUTE_STALE_AFTER_MS && !routeRecheck && mine === generation && !mutationBusy) startRouteRecheck(tunnelKey);
        }
        const verdictAge = lastRouteCheck ? now() - lastRouteCheck : null;
        const verified = routeVerification.verified && verdictAge !== null && verdictAge < ROUTE_VERDICT_MAX_AGE_MS;
        return {
          connected: !installerUnknown && tunnelActive && discordRunning && verified && mine === generation && !mutationBusy,
          tunnelActive,
          hasOwnedTunnel: ownPresent,
          requiresElevation: deps.requiresElevation === true,
          readiness: tunnelActive ? (discordRunning && verified ? "verified" : "unverified") : "inactive",
          discordRunning,
          stage,
          externalTunnel: status?.reliable === true && status.active === true && status.owned !== true,
          reliable: installerUnknown ? false : status?.reliable === true,
          installerUnknown,
          signedIn: Boolean(currentUsername),
          username: currentUsername,
          route: state.route,
          mode: state.mode,
          routeCheckedAgeMs: verdictAge,
          routeRechecking: routeRecheck !== null,
        };
      }

      if (command === "diagnostics") {
        const status = await inspect();
        const lines = [
          `WireSock inspection: ${status?.reliable ? "reliable" : "unreliable"}.`,
          `Tunnel: ${status?.active ? (status.owned ? "native-owned" : "external") : "inactive"}.`,
          `Discord session: ${sessionApps.length ? "restarted by Brisa" : "not confirmed"}.`,
          `Route check: ${routeVerification.reason}.`,
          `Connection stage: ${stage}.`,
          `Mode: ${state.mode}.`,
          `Packaged Proton helper: ${deps.helperAvailable() ? "available" : "missing"}.`,
        ];
        const events = deps.diag?.recent?.(20) ?? [];
        return { text: lines.concat(events.length ? ["Recent events (this session):", ...events] : []).join("\n") };
      }

      if (mutationBusy) return { success: false, message: "Another native backend operation is still running." };
      mutationBusy = true;
      mutationDone = new Promise(resolve => { releaseMutation = resolve; });
      const connectOperation = command === "connect" ? begin() : null;
      try {
        const guarded = command === "connect" || command === "disconnect" ? await mutationGuard()
          : command === "login" || command === "importConfig" ? { status: null }
          : command === "logout" ? await profileMutationGuard("Disconnect the native tunnel before signing out.")
          : await profileMutationGuard("Disconnect before optimizing; active tunnels are never switched automatically.");
        if (connectOperation && !connectOperation.isCurrent()) return cancelled();
        if (guarded.failure) return guarded.failure;

      if (command === "login") {
        if (!deps.helperAvailable()) return { success: false, message: "The packaged Proton helper is unavailable." };
        const operation = begin();
        try {
          const result = await deps.login(paths.dataDir, payload.username, payload.password, payload.twoFactorCode,
            payload.humanVerificationToken, { signal: operation.controller.signal, isCurrent: operation.isCurrent }, payload.humanVerificationMethod);
          if (!operation.isCurrent()) return cancelled();
          return actionResult(result);
        } finally { finish(operation); }
      }

      if (command === "logout") {
        if (guarded.status.active && guarded.status.owned) return { success: false, message: "Disconnect the native tunnel before signing out." };
        await deps.files.removeOwned(paths.sessionPath);
        await deps.files.removeOwned(`${paths.sessionPath}.lock`);
        await deps.files.removeOwned(paths.generatedProfilePath);
        // The native profile may still be in use by a tunnel this process cannot inspect.
        // It holds a private key, so it is only removed on reliable proof that no native
        // tunnel remains; an unreadable state is not proof of absence.
        const nativeAbsent = guarded.status?.reliable === true && !hasOwned(guarded.status);
        if (nativeAbsent) await deps.files.removeOwned(paths.ownedConfigPath);
        state = { ...state, route: null };
        await saveState();
        return nativeAbsent ? { success: true }
          : { success: true, message: "Signed out. Brisa kept its native tunnel profile because tunnel ownership could not be verified. Open Brisa as administrator and sign out again to finish clearing that profile." };
      }

      if (command === "optimize") {
        if (guarded.status.active) return { success: false, message: "Disconnect before optimizing; active tunnels are never switched automatically." };
        const operation = begin();
        try { return await generate(payload, operation); }
        catch { return operation.isCurrent() ? { success: false, message: safeFailure() } : cancelled(); }
        finally { finish(operation); }
      }

      if (command === "importConfig") {
        const selected = payload.path;
        if (!(path.isAbsolute(selected) || path.win32.isAbsolute(selected)) || path.extname(selected).toLowerCase() !== ".conf") {
          return { success: false, message: "Select an absolute WireGuard .conf file." };
        }
        let raw;
        try { raw = await deps.files.readExplicit(selected); }
        catch { return { success: false, message: "The selected WireGuard file could not be read." }; }
        const validation = deps.validateConfig(raw);
        if (!validation?.valid) {
          note("preparing-profile", validation?.code ?? "PROFILE_INVALID");
          return profileFailure(validation, false);
        }
        let apps;
        try { apps = await discordApps(); }
        catch (error) { return discordCompatibilityFailure(error); }
        const sanitized = deps.sanitizeConfig(raw, apps.join(", "));
        await deps.files.writeOwned(paths.importedProfilePath, sanitized);
        state.mode = "custom";
        state.route = null;
        await saveState();
        return { success: true };
      }

      if (command === "connect") {
        const operation = connectOperation;
        if (!operation?.isCurrent()) return cancelled();
        if (guarded.status.active && guarded.status.owned)
          return { success: false, message: "A Brisa tunnel is already active. Disconnect it before starting a fresh Discord connection." };
        let apps;
        try { apps = await discordApps(); }
        catch (error) { return operation.isCurrent() ? discordCompatibilityFailure(error) : cancelled(); }
        if (!operation.isCurrent()) return cancelled();
        stage = "preparing-profile";
        if (state.mode === "proton") {
          const currentUsername = await username();
          if (!operation.isCurrent()) return cancelled();
          if (!currentUsername) return { success: false, message: "Sign in to ProtonVPN first." };
        }
        let profilePath = state.mode === "custom" ? paths.importedProfilePath : paths.generatedProfilePath;
        const profileExists = await deps.files.existsOwned(profilePath);
        if (!operation.isCurrent()) return cancelled();
        if (!profileExists) {
          if (state.mode === "custom") return { success: false, message: "Import a WireGuard profile first." };
          try {
            const result = await generate(payload, operation);
            if (!result.success) return result;
          } catch { return operation.isCurrent() ? { success: false, message: safeFailure() } : cancelled(); }
          if (!operation.isCurrent()) return cancelled();
        }
        // Route optimization may outlast a Discord update. Resolve again rather
        // than retaining a versioned path captured before profile generation.
        try { apps = await discordApps(); }
        catch (error) { return operation.isCurrent() ? discordCompatibilityFailure(error) : cancelled(); }
        if (!operation.isCurrent()) return cancelled();
        const raw = await deps.files.readOwned(profilePath);
        if (!operation.isCurrent()) return cancelled();
        const validation = deps.validateConfig(raw);
        if (!validation?.valid) {
          note("preparing-profile", validation?.code ?? "PROFILE_INVALID");
          return profileFailure(validation, true);
        }
        try { await deps.validateDiscordApps?.(apps); }
        catch (error) { return operation.isCurrent() ? discordCompatibilityFailure(error) : cancelled(); }
        if (!operation.isCurrent()) return cancelled();
        sessionApps = [];
        resetRoute();
        try {
          stage = "closing-discord";
          await deps.stopDiscord(apps, operation.controller.signal);
          if (!operation.isCurrent()) return await cancelledStart(apps);
          stage = "starting-tunnel";
          // Recorded before the start attempt: a start that dies halfway can leave a tunnel
          // this session owns but can no longer prove, and that must still block profile
          // operations until a reliable inspection or a confirmed stop clears it.
          sessionOwned = true;
          // O backend só aceita estes dois estágios vindos do start, e só de uma operação ainda
          // corrente: um callback atrasado não pode reescrever o estágio de outra operação.
          const onStage = nextStage => {
            if (!operation.isCurrent()) return;
            if (nextStage !== "installing-wiresock" && nextStage !== "starting-tunnel") return;
            stage = nextStage;
          };
          await deps.start(paths.ownedConfigPath, raw, apps, operation.controller.signal, { onStage });
          if (!operation.isCurrent()) return await cancelledStart(apps);
          stage = "starting-discord";
          await deps.launchDiscord(apps, operation.controller.signal);
          if (!operation.isCurrent()) return await cancelledStart(apps);
          sessionApps = apps;
          stage = "verifying-route";
          const proved = await checkRoute(operation.controller.signal);
          if (!operation.isCurrent()) return await cancelledStart(apps);
          // A discarded proof means the tunnel it proved is gone or was replaced.
          if (!proved) throw new Error("Route proof discarded for a tunnel that is no longer active.");
          if (!routeVerification.verified) {
            note("verifying-route", "ROUTE_UNVERIFIED", { reason: String(routeVerification.reason).toUpperCase() });
            return { success: false, code: "ROUTE_UNVERIFIED",
              message: "Discord was reopened and the tunnel is running, but the routed connection could not be verified. Disconnect and try another route." };
          }
          return { success: true, ...(state.route ? { route: state.route } : {}) };
        } catch (error) {
          // Antes de qualquer cancelamento: sem prova de saída do instalador, nada de rollback
          // nem de "Operation cancelled.", e o estado fica desconhecido até reiniciar.
          if (error?.name === "InstallerSupervisionLost") {
            installerUnknown = true;
            note("installing-wiresock", "SDK_INSTALL_SUPERVISION_LOST");
            return { success: false, code: "SDK_INSTALL_SUPERVISION_LOST",
              message: "WireSock installation status is unknown. Brisa will stay open; restart Brisa before trying again." };
          }
          if (!operation.isCurrent()) return await cancelledStart(apps);
          const failedStage = stage;
          const cleaned = await rollbackStart(apps);
          if (error instanceof DiscordCompatibilityError || error instanceof WireSockSetupError) {
            const failure = error instanceof WireSockSetupError ? wireSockSetupFailure(error) : discordCompatibilityFailure(error);
            return { ...failure, message: cleaned ? failure.message
              : `${failure.message} Brisa could not confirm tunnel cleanup and Discord restoration. Check the connection status before retrying.` };
          }
          if (error instanceof WireSockStartError) {
            note(failedStage, error.code, error.data);
            return startFailure(error, cleaned);
          }
          note(failedStage, "START_FAILED_UNCLASSIFIED");
          return { success: false, message: !cleaned
            ? "Connection failed. Tunnel cleanup and Discord restoration could not both be confirmed. Check the connection status before retrying."
            : failedStage === "closing-discord" ? "Discord could not be closed within the startup window. No tunnel was started, and Discord was restored. Try again."
            : failedStage === "starting-discord" ? "Discord could not be restarted on the VPN. Brisa restored the normal connection. Try another route."
            : "The Brisa tunnel could not be started. Check WireSock and your VPN profile, then try again." };
        }
      }

      if (command === "disconnect") {
        generation += 1;
        if (!guarded.status.active && !hasOwned(guarded.status)) return { success: true };
        if (!guarded.status.active) return { success: false, message: "Brisa's tunnel may still be running, but ownership could not be confirmed. Run Brisa as administrator and retry. Brisa will stay open." };
        const apps = sessionApps.length ? sessionApps : await deps.discoverDiscord();
        sessionApps = [];
        resetRoute();
        let discordClosed = false;
        try {
          stage = "closing-discord";
          if (apps.length) {
            await deps.stopDiscord(apps, undefined, { cleanup: true });
            discordClosed = true;
          }
          stage = "stopping-tunnel";
          const result = await deps.stop(paths.ownedConfigPath);
          if (result?.stopped === true) sessionOwned = false;
          note("stop", result?.stopped === true ? "STOP_CONFIRMED" : "STOP_NOT_CONFIRMED", {
            rollback: false,
            services: result?.servicesResidual?.length ?? 0,
            processes: result?.processResidual?.length ?? 0,
            networkLockReset: result?.networkLockReset === true,
          });
          if (result?.stopped !== true) return { success: false, message: discordClosed
            ? "Discord was closed, but the native-owned tunnel could not be stopped safely. Brisa will stay open. Check the tunnel status before reopening Discord."
            : "The native-owned tunnel could not be stopped safely. Brisa will stay open. Check the tunnel status before retrying." };
          stage = "starting-discord";
          if (apps.length) await deps.launchDiscord(apps);
          return { success: true };
        } catch {
          if (stage === "starting-discord") return { success: false, message: "The tunnel was stopped, but Discord could not be reopened. Open Discord manually." };
          note("stop", "STOP_NOT_CONFIRMED", { rollback: false, discordClosed });
          return { success: false, message: discordClosed
            ? "Discord was closed, but the native-owned tunnel could not be stopped safely. Brisa will stay open. Check the tunnel status before reopening Discord."
            : "Discord or the tunnel could not be stopped safely. Close Discord and try disconnecting again." };
        }
      }

        throw new Error("Unknown command.");
      } finally {
        if (connectOperation) finish(connectOperation);
        mutationBusy = false;
        // Com o instalador desconhecido o estágio não volta para idle: idle seria uma promessa
        // de que nada nativo está em andamento.
        if (!installerUnknown) stage = "idle";
        releaseMutation();
      }
    },
  };
}
