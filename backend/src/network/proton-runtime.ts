import fs from "node:fs";
import path from "node:path";

export type ProtonRuntimeContext = {
  resourcesPath?: string;
  appPath?: string;
  execPath?: string;
  cwd?: string;
  moduleDir?: string;
  platform?: string;
  arch?: string;
};

const helperName = (platform: string) => platform === "win32" ? "proton-confgen.exe" : "proton-confgen";

export function protonConfgenCandidates(context: ProtonRuntimeContext): string[] {
  const name = helperName(context.platform || process.platform);
  return [
    context.resourcesPath && path.join(context.resourcesPath, "extra", "proton-confgen", name),
    context.resourcesPath && path.join(context.resourcesPath, "extra", name),
  ].filter(Boolean);
}

export function findProtonConfgenPath(context: ProtonRuntimeContext): string | undefined {
  return protonConfgenCandidates(context).find(candidate => {
    try {
      const stat = fs.lstatSync(candidate);
      return stat.isFile() && !stat.isSymbolicLink() && stat.size > 0;
    } catch { return false; }
  });
}

export async function ensureProtonConfgen({ context }: {
  context: ProtonRuntimeContext;
  installDir?: string;
  version?: string;
}): Promise<string> {
  const found = findProtonConfgenPath(context);
  if (!found) throw new Error("The packaged Proton helper is unavailable.");
  return found;
}
