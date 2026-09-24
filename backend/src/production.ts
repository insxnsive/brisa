import { spawn, execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import {
  generateOptimalProtonConfig,
  getProtonPlan,
  getSavedSessionUsername,
  loginProton,
} from "./network/proton.ts";
import { findWindowsDiscordInstall } from "./network/windows-discord-install.ts";
import {
  inspectWireSockAsync,
  stopOwnedWireSock,
} from "./network/vpn-windows.ts";
import {
  sanitizeWireGuardConfig,
  validateWireGuardConfig,
} from "./network/vpn-types.ts";
import { createBackend } from "./backend.mjs";
import { startNativeWireSock } from "./native-wiresock-start.ts";

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
  const configured = process.env.BRISA_DATA_DIR;
  const base = configured || (process.env.LOCALAPPDATA && path.join(process.env.LOCALAPPDATA, "Brisa"));
  if (!base || !path.isAbsolute(base)) throw new Error("BRISA_DATA_DIR must be an absolute path.");
  const resolved = path.resolve(base);
  fs.mkdirSync(resolved, { recursive: true, mode: 0o700 });
  return resolved;
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

  return createBackend({
    inspect: async (configPath: string) => elevated ? inspectWireSockAsync(configPath) : { active: false, owned: false, reliable: false, services: [], processIds: [], reason: "Run Brisa as administrator to manage WireSock safely.", origin: "unknown", managedServices: [], managedProcessIds: [] },
    getSavedSessionUsername,
    getPlan: getProtonPlan,
    login: loginProton,
    generate: generateOptimalProtonConfig,
    validateConfig: validateWireGuardConfig,
    sanitizeConfig: sanitizeWireGuardConfig,
    helperAvailable: () => existingRegularFile(helperPath),
    discoverDiscord: async () => discordExecutables(),
    launchDiscord: async (executable: string) => {
      if (!discordExecutables().some(candidate => candidate.toLowerCase() === executable.toLowerCase())) throw new Error("Discord path is not trusted.");
      const child = spawn(executable, [], { detached: true, stdio: "ignore", windowsHide: true, shell: false });
      child.unref();
    },
    start: startNativeWireSock,
    stop: (configPath: string) => stopOwnedWireSock(configPath, noLog),
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
