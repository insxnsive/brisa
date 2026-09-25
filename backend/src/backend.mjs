import path from "node:path";

const METHODS = new Set(["captcha", "ownership-email", "ownership-sms"]);
const COMMANDS = new Set(["snapshot", "login", "logout", "optimize", "connect", "disconnect", "importConfig", "cancel", "diagnostics", "progress", "waitForIdle"]);

const cleanText = (value, fallback = "Operation failed.", secrets = []) => {
  let text = typeof value === "string" ? value : fallback;
  text = text.replace(/[\r\n\t]+/g, " ").replace(/(PrivateKey\s*=\s*)\S+/gi, "$1<redacted>")
    .replace(/(password|token|secret|authorization|session)\s*[:=]\s*\S+/gi, "$1=<redacted>");
  for (const secret of secrets) if (typeof secret === "string" && secret.length >= 3) text = text.split(secret).join("<redacted>");
  text = text.trim().slice(0, 400);
  return text || fallback;
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
  if (!server && !country) return null;
  const route = { server, country };
  if (Number.isFinite(value.pingMs) && value.pingMs >= 0 && value.pingMs <= 60_000) route.pingMs = Math.round(value.pingMs);
  return route;
};

const actionResult = (value, secrets = []) => {
  const result = { success: value?.success === true };
  if (typeof value?.code === "string" && /^[A-Z0-9_]{1,64}$/.test(value.code)) result.code = value.code;
  if (typeof value?.message === "string" || typeof value?.error === "string") result.message = cleanText(value.message || value.error, "Operation failed.", secrets);
  const captchaUrl = safeCaptchaUrl(value?.captchaUrl);
  if (captchaUrl) result.captchaUrl = captchaUrl;
  const route = safeRoute(value?.route || value);
  if (route) result.route = route;
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
  let lastRouteCheck = 0;
  let routeCheckInFlight = null;
  const now = () => deps.now?.() ?? Date.now();
  const checkRoute = async signal => {
    const mine = generation;
    let result;
    try { result = await deps.verifyRoute(signal); }
    catch { result = { verified: false, reason: "probe_failed" }; }
    if (mine === generation) {
      routeVerification = { verified: result?.verified === true,
        reason: ["verified", "probe_failed", "discord_failed", "same_as_direct", "probe_unavailable"].includes(result?.reason) ? result.reason : "probe_failed" };
      lastRouteCheck = now();
    }
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
    if (!status?.reliable) return { status, failure: { success: false, message: "WireSock state could not be inspected reliably; no changes were made." } };
    if (status.active && !status.owned) return { status, failure: { success: false, message: "Another WireSock tunnel is active; no changes were made." } };
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
    if (!valid.length) throw new Error("No supported Discord executable was found.");
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
    } catch {}
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
    const normalized = actionResult(generated, [payload.humanVerificationToken]);
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
    sessionApps = [];
    routeVerification = { verified: false, reason: "not_checked" };
    try {
      const status = await inspect();
      if (!status?.reliable || (status.active && !status.owned)) return false;
      if (status.active) {
        await deps.stopDiscord(apps);
        if ((await deps.stop(paths.ownedConfigPath))?.stopped !== true) return false;
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

  return {
    async execute(command, payload) {
      validateCommandPayload(command, payload);
      const enteredGeneration = generation;
      if (command === "cancel") {
        generation += 1;
        activeOperation?.controller.abort();
        activeOperation = null;
        return { success: true };
      }
      if (command === "waitForIdle") { await mutationDone; return { success: true }; }
      if (command === "progress") return { success: true, stage };
      await loadState();
      if (["login", "optimize", "connect"].includes(command) && generation !== enteredGeneration) return cancelled();

      if (command === "snapshot") {
        const mine = generation;
        const status = await inspect();
        const currentUsername = await username();
        const tunnelActive = status?.reliable === true && status.active === true && status.owned === true;
        let discordRunning = false;
        if (tunnelActive && sessionApps.length && !mutationBusy) {
          try { discordRunning = await deps.discordRunning(sessionApps); } catch {}
          if (discordRunning && now() - lastRouteCheck >= 30_000 && mine === generation && !mutationBusy) {
            routeCheckInFlight ??= checkRoute().finally(() => { routeCheckInFlight = null; });
            await routeCheckInFlight;
          }
        }
        if (!tunnelActive && mine === generation && !mutationBusy) { sessionApps = []; routeVerification = { verified: false, reason: "not_checked" }; }
        return {
          connected: tunnelActive && discordRunning && routeVerification.verified && mine === generation && !mutationBusy,
          tunnelActive,
          readiness: tunnelActive ? (discordRunning && routeVerification.verified ? "verified" : "unverified") : "inactive",
          discordRunning,
          stage,
          externalTunnel: status?.reliable === true && status.active === true && status.owned !== true,
          reliable: status?.reliable === true,
          signedIn: Boolean(currentUsername),
          username: currentUsername,
          route: state.route,
          mode: state.mode,
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
        return { text: cleanText(lines.join("\n"), "Diagnostics unavailable.", []) };
      }

      if (mutationBusy) return { success: false, message: "Another native backend operation is still running." };
      mutationBusy = true;
      mutationDone = new Promise(resolve => { releaseMutation = resolve; });
      const connectOperation = command === "connect" ? begin() : null;
      try {
        const guarded = await mutationGuard();
        if (connectOperation && !connectOperation.isCurrent()) return cancelled();
        if (guarded.failure) return guarded.failure;

      if (command === "login") {
        if (!deps.helperAvailable()) return { success: false, message: "The packaged Proton helper is unavailable." };
        const operation = begin();
        try {
          const result = await deps.login(paths.dataDir, payload.username, payload.password, payload.twoFactorCode,
            payload.humanVerificationToken, { signal: operation.controller.signal, isCurrent: operation.isCurrent }, payload.humanVerificationMethod);
          if (!operation.isCurrent()) return cancelled();
          return actionResult(result, [payload.username, payload.password, payload.twoFactorCode, payload.humanVerificationToken]);
        } finally { finish(operation); }
      }

      if (command === "logout") {
        if (guarded.status.active && guarded.status.owned) return { success: false, message: "Disconnect the native tunnel before signing out." };
        await deps.files.removeOwned(paths.sessionPath);
        await deps.files.removeOwned(`${paths.sessionPath}.lock`);
        await deps.files.removeOwned(paths.generatedProfilePath);
        await deps.files.removeOwned(paths.ownedConfigPath);
        state = { ...state, route: null };
        await saveState();
        return { success: true };
      }

      if (command === "optimize") {
        if (guarded.status.active) return { success: false, message: "Disconnect before optimizing; active tunnels are never switched automatically." };
        const operation = begin();
        try { return await generate(payload, operation); }
        catch (error) { return operation.isCurrent() ? { success: false, message: cleanText(error) } : cancelled(); }
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
        if (!validation?.valid) return { success: false, message: cleanText(validation?.error, "The WireGuard profile is invalid.") };
        const apps = await discordApps();
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
          } catch (error) { return operation.isCurrent() ? { success: false, message: cleanText(error) } : cancelled(); }
          if (!operation.isCurrent()) return cancelled();
        }
        const apps = await discordApps();
        if (!operation.isCurrent()) return cancelled();
        const raw = await deps.files.readOwned(profilePath);
        if (!operation.isCurrent()) return cancelled();
        const validation = deps.validateConfig(raw);
        if (!validation?.valid) return { success: false, message: "The saved WireGuard profile is invalid." };
        sessionApps = [];
        routeVerification = { verified: false, reason: "not_checked" };
        try {
          stage = "closing-discord";
          await deps.stopDiscord(apps, operation.controller.signal);
          if (!operation.isCurrent()) return await cleanupCancelledStart(apps);
          stage = "starting-tunnel";
          await deps.start(paths.ownedConfigPath, raw, apps, operation.controller.signal);
          if (!operation.isCurrent()) return await cleanupCancelledStart(apps);
          stage = "starting-discord";
          await deps.launchDiscord(apps, operation.controller.signal);
          if (!operation.isCurrent()) return await cleanupCancelledStart(apps);
          sessionApps = apps;
          stage = "verifying-route";
          await checkRoute(operation.controller.signal);
          if (!operation.isCurrent()) return await cleanupCancelledStart(apps);
          if (!routeVerification.verified) return { success: false, code: "ROUTE_UNVERIFIED",
            message: "Discord was reopened and the tunnel is running, but the routed connection could not be verified. Disconnect and try another route." };
          return { success: true, ...(state.route ? { route: state.route } : {}) };
        } catch {
          if (!operation.isCurrent()) return await cleanupCancelledStart(apps);
          const failedStage = stage;
          const cleaned = await rollbackStart(apps);
          return { success: false, message: !cleaned
            ? "Connection failed. Tunnel cleanup and Discord restoration could not both be confirmed. Check the connection status before retrying."
            : failedStage === "closing-discord" ? "Discord could not be closed within the startup window. No tunnel was started, and Discord was restored. Try again."
            : failedStage === "starting-discord" ? "Discord could not be restarted on the VPN. Brisa restored the normal connection. Try another route."
            : "The Brisa tunnel could not be started. Check WireSock and your VPN profile, then try again." };
        }
      }

      if (command === "disconnect") {
        generation += 1;
        if (!guarded.status.active) return { success: true };
        const apps = sessionApps.length ? sessionApps : await deps.discoverDiscord();
        sessionApps = [];
        routeVerification = { verified: false, reason: "not_checked" };
        try {
          stage = "closing-discord";
          if (apps.length) await deps.stopDiscord(apps);
          stage = "stopping-tunnel";
          const result = await deps.stop(paths.ownedConfigPath);
          if (result?.stopped !== true) return { success: false, message: "The native-owned tunnel could not be stopped safely." };
          stage = "starting-discord";
          if (apps.length) await deps.launchDiscord(apps);
          return { success: true };
        } catch {
          return { success: false, message: stage === "starting-discord"
            ? "The tunnel was stopped, but Discord could not be reopened. Open Discord manually."
            : "Discord or the tunnel could not be stopped safely. Close Discord and try disconnecting again." };
        }
      }

        throw new Error("Unknown command.");
      } finally {
        if (connectOperation) finish(connectOperation);
        mutationBusy = false;
        stage = "idle";
        releaseMutation();
      }
    },
  };
}
