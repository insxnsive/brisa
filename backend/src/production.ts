import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { setTimeout as delay } from "node:timers/promises";
import { createDiscordLifecycle, discordAllowedApps } from "./discord-lifecycle.mjs";
import { createDiscordSelectionGuard } from "./discord-compatibility.mjs";
import { createRouteVerifier } from "./route-verification.mjs";

import {
  generateOptimalProtonConfig,
  getProtonPlan,
  getSavedSessionUsername,
  loginProton,
} from "./network/proton.ts";
import { findWindowsDiscordInstall } from "./network/windows-discord-install.ts";
import {
  inspectWireSock,
  inspectWireSockAsync,
  stopOwnedWireSock,
  type WireSockCandidateLease,
} from "./network/vpn-windows.ts";
import {
  sanitizeWireGuardConfig,
  validateWireGuardConfig,
} from "./network/vpn-types.ts";
import { createBackend } from "./backend.mjs";
import { createDiagnostics } from "./diagnostics.mjs";
import { startNativeWireSock } from "./native-wiresock-start.ts";
import { prepareNativeDataStoreSync } from "./native-data-store.mjs";

const MAX_CONFIG_BYTES = 512 * 1024;

function existingRegularFile(target: string): boolean {
  try {
    const stat = fs.lstatSync(target);
    return stat.isFile() && !stat.isSymbolicLink() && stat.size > 0;
  } catch { return false; }
}

function within(root: string, target: string): boolean {
  const relative = path.relative(root, target);
  return relative === "" || (!path.isAbsolute(relative) && relative !== ".." && !relative.startsWith(`..${path.sep}`));
}

function safeDataDir(): string {
  if (process.platform !== "win32") throw new Error("Brisa private data requires Windows NTFS ACLs.");
  const configured = process.env.BRISA_DATA_DIR;
  const base = configured || (process.env.LOCALAPPDATA && path.join(process.env.LOCALAPPDATA, "Brisa"));
  if (!base || !path.isAbsolute(base)) throw new Error("BRISA_DATA_DIR must be an absolute path.");
  return prepareNativeDataStoreSync(base, { probeLegacyTunnel: (oldConfigPath: string) => inspectWireSock(oldConfigPath) });
}

function safeResourceDir(): string {
  const value = process.env.BRISA_RESOURCE_DIR || path.join(process.cwd(), "resources");
  if (!path.isAbsolute(value)) throw new Error("BRISA_RESOURCE_DIR must be an absolute path.");
  return path.resolve(value);
}

function discordExecutables(): string[] {
  if (process.platform !== "win32") return [];
  const flavours = ["Discord", "DiscordPTB", "DiscordCanary", "Vesktop", "Equibop", "Legcord"];
  const allowedNames = new Set(flavours.map(value => `${value}.exe`.toLowerCase()));
  const found = new Map<string, string>();
  const explicit = process.env.BRISA_DISCORD_EXE;
  if (explicit && explicit.length <= 2048 && path.isAbsolute(explicit) && allowedNames.has(path.basename(explicit).toLowerCase()) && existingRegularFile(explicit)) {
    found.set(explicit.toLowerCase(), explicit);
  }
  const bases = [process.env.LOCALAPPDATA, process.env.ProgramW6432, process.env.ProgramFiles, process.env["ProgramFiles(x86)"]]
    .filter((value): value is string => Boolean(value));
  for (const base of bases) {
    for (const flavour of flavours) {
      for (const root of [path.join(base, flavour), path.join(base, "Programs", flavour), path.join(base, flavour.toLowerCase())]) {
        const install = findWindowsDiscordInstall(root, flavour);
        if (!install || !existingRegularFile(install.exePath)) continue;
        found.set(install.exePath.toLowerCase(), install.exePath);
      }
    }
  }
  return [...found.values()];
}

export function createProductionBackend() {
  const dataDir = safeDataDir();
  const resourceDir = safeResourceDir();
  Object.defineProperty(process, "resourcesPath", { value: resourceDir, configurable: true });
  const helperPath = path.join(resourceDir, "extra", "proton-confgen", "proton-confgen.exe");
  const paths = {
    dataDir,
    statePath: path.join(dataDir, "state.json"),
    sessionPath: path.join(dataDir, "proton-session.json"),
    ownedConfigPath: path.join(dataDir, "native-wiresock.conf"),
    importedProfilePath: path.join(dataDir, "imported.conf"),
    generatedProfilePath: path.join(dataDir, "wireguard.conf"),
  };
  const assertOwned = (target: string) => {
    const resolved = path.resolve(target);
    if (!within(dataDir, resolved)) throw new Error("Owned path escaped the native data directory.");
    return resolved;
  };
  const noLog = () => {};
  // Codes and numbers only, capped at 128 KiB, inside the private data directory. It is the
  // only record that survives the app being closed after a failed connect.
  const diagnostics = createDiagnostics({ file: path.join(dataDir, "diagnostics.log") });
  const discord = createDiscordLifecycle();
  const verifier = createRouteVerifier({ helperPath, probePath: path.join(dataDir, "brisa-route-probe.exe") });
  let probePrepared = false;
  const selectionGuard = createDiscordSelectionGuard(discordExecutables);
  const trustedApps = selectionGuard.assertCurrent;
  // WireSock is elevated. A standard user cannot reliably inspect its command
  // line, so never let a start succeed before discovering that ownership is
  // unreadable. Check the current token only; never request elevation here.
  let elevated = false;
  try {
    const script = "([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)";
    elevated = execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
      encoding: "utf8", windowsHide: true, timeout: 5000, stdio: ["ignore", "pipe", "ignore"],
    }).trim().toLowerCase() === "true";
  } catch { /* Unknown privilege fails closed, just like unknown ownership. */ }
  // Every mutation refuses while unelevated; record why so the refusal is diagnosable.
  if (!elevated) diagnostics.record("startup", "NOT_ELEVATED");

  // Lease da sessão: o par que este start comprovou, com os digests aprendidos agora. O stop
  // usa esse lease para o reset opcional em vez de descobrir o SDK no meio de uma parada.
  let sessionLease: WireSockCandidateLease | null = null;

  return createBackend({
    diag: diagnostics,
    // Fatos que o log de suporte pode citar. O que não existe aqui é omitido, nunca inventado.
    supportFacts: () => ({ elevated, ...(sessionLease?.trustVerdict ? { trustVerdict: sessionLease.trustVerdict } : {}) }),
    requiresElevation: !elevated,
    inspect: async (configPath: string) => elevated ? inspectWireSockAsync(configPath) : { active: false, owned: false, reliable: false, services: [], processIds: [], reason: "Run Brisa as administrator to manage WireSock safely.", origin: "unknown", managedServices: [], managedProcessIds: [] },
    getSavedSessionUsername,
    getPlan: getProtonPlan,
    login: loginProton,
    generate: generateOptimalProtonConfig,
    validateConfig: validateWireGuardConfig,
    sanitizeConfig: sanitizeWireGuardConfig,
    helperAvailable: () => existingRegularFile(helperPath),
    discoverDiscord: async () => {
      const all = discordExecutables();
      if (!all.length) return [];
      const running = await discord.runningApps(all);
      // Restart the clients the user is actually using, not every installed
      // PTB/Canary/alternative. Stable Discord is first if none is running.
      return running.length ? running : all.slice(0, 1);
    },
    validateDiscordApps: trustedApps,
    stopDiscord: async (apps: string[], signal?: AbortSignal, { cleanup = false }: { cleanup?: boolean } = {}) => {
      (cleanup ? selectionGuard.assertCleanup : trustedApps)(apps);
      await discord.stop(apps, signal);
    },
    launchDiscord: async (apps: string[], signal?: AbortSignal) => { trustedApps(apps); await discord.launch(apps, signal); },
    discordRunning: (apps: string[]) => discord.isRunning(apps),
    verifyRoute: (signal?: AbortSignal) => probePrepared ? verifier.verify(signal) : Promise.resolve({ verified: false, reason: "probe_unavailable" }),
    start: async (configPath: string, raw: string, apps: string[], signal?: AbortSignal, options?: { onStage?: (stage: string) => void }) => {
      trustedApps(apps);
      const scoped = discordAllowedApps(apps);
      const allowed = [...scoped.executables, ...scoped.appDirs, ...scoped.executableNames, ...scoped.updaterPaths];
      probePrepared = false;
      try {
        const probe = await verifier.prepare(signal);
        allowed.push(probe, path.basename(probe));
        probePrepared = true;
      } catch { signal?.throwIfAborted(); }
      // O SDK é a única parte do start que pode abrir uma janela do fornecedor: seus eventos
      // entram no diagnóstico persistente com o estágio installing-wiresock e só com códigos.
      const onEvent = (code: string, data?: Record<string, number | boolean>) => diagnostics.record("installing-wiresock", code, data);
      // A lease é publicada antes do lançamento: o rollback e o stop de uma sessão que falhou
      // também precisam da linha de base, e aprender depois carimbaria bytes não validados.
      const onLease = (lease: WireSockCandidateLease) => { sessionLease = lease; };
      const started = await startNativeWireSock(configPath, raw, allowed, signal, { ...options, onEvent, onLease });
      // Keep the proven Windows settling interval before opening fresh sockets.
      await delay(2_000, undefined, { signal });
    },
    stop: async (configPath: string, options?: { signal?: AbortSignal; timeoutMs?: number; candidateLease?: WireSockCandidateLease | null }) => {
      const result = await stopOwnedWireSock(configPath, noLog, { ...options, candidateLease: options?.candidateLease ?? sessionLease });
      if (result?.stopped === true) sessionLease = null;
      return result;
    },
    files: {
      readExplicit: async (selected: string) => {
        if (!(path.isAbsolute(selected) || path.win32.isAbsolute(selected)) || path.extname(selected).toLowerCase() !== ".conf") throw new Error("Invalid path.");
        const stat = await fs.promises.lstat(selected);
        if (!stat.isFile() || stat.isSymbolicLink() || stat.size <= 0 || stat.size > MAX_CONFIG_BYTES) throw new Error("Invalid file.");
        return fs.promises.readFile(selected, "utf8");
      },
      readOwned: async (target: string) => fs.promises.readFile(assertOwned(target), "utf8"),
      writeOwned: async (target: string, value: string) => {
        const destination = assertOwned(target);
        const temporary = `${destination}.${process.pid}.${Date.now()}.tmp`;
        try {
          await fs.promises.writeFile(temporary, value, { encoding: "utf8", mode: 0o600, flag: "wx" });
          await fs.promises.rm(destination, { force: true });
          await fs.promises.rename(temporary, destination);
        } finally {
          await fs.promises.rm(temporary, { force: true }).catch(() => {});
        }
      },
      removeOwned: async (target: string) => fs.promises.rm(assertOwned(target), { force: true }),
      existsOwned: async (target: string) => existingRegularFile(assertOwned(target)),
    },
  }, paths);
}
