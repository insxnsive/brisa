import { execFile } from "node:child_process";
import fs from "node:fs/promises";
import path from "node:path";
import { isIP } from "node:net";

const inconclusive = () => ({ verified: false, reason: "probe_failed" });
const observations = result => (Array.isArray(result?.observations) ? result.observations : [])
  .filter(item => item && typeof item.source === "string" && item.source && typeof item.ip === "string" && isIP(item.ip));

// A successful request from Brisa itself is outside AllowedApps and proves nothing.
// Require a different egress from the explicitly included, separately named probe.
export function verifyRouteEvidence(direct, tunneled) {
  if (!direct?.success || !tunneled?.success) return inconclusive();
  if (!tunneled.discordOk) return { verified: false, reason: "discord_failed" };
  const baseline = new Map(observations(direct).map(item => [item.source, item.ip]));
  const comparable = observations(tunneled).filter(item => baseline.has(item.source));
  if (!comparable.length) return inconclusive();
  if (comparable.some(item => baseline.get(item.source) === item.ip)) return { verified: false, reason: "same_as_direct" };
  return { verified: true, reason: "verified" };
}

function runProbe(executable, signal) {
  return new Promise((resolve, reject) => {
    execFile(executable, ["--route-probe", "--json"], { windowsHide: true, timeout: 12_000, maxBuffer: 128 * 1024, encoding: "utf8", signal }, (error, stdout) => {
      if (error) { reject(new Error("Route probe unavailable.")); return; }
      try { resolve(JSON.parse(stdout)); } catch { reject(new Error("Route probe unavailable.")); }
    });
  });
}

async function copyProbe(source, destination) {
  // Replace the owned helper atomically; never follow a pre-existing file symlink.
  const temporary = `${destination}.${process.pid}.tmp`;
  try {
    await fs.copyFile(source, temporary, 1 /* COPYFILE_EXCL */);
    await fs.rename(temporary, destination);
  } finally { await fs.rm(temporary, { force: true }).catch(() => {}); }
}

export function createRouteVerifier({ helperPath, probePath, copy = copyProbe, runProbe: probe = runProbe }) {
  if (path.resolve(helperPath).toLowerCase() === path.resolve(probePath).toLowerCase())
    throw new Error("The route probe must not replace the Proton account helper.");
  return {
    async prepare(signal) {
      signal?.throwIfAborted();
      await copy(helperPath, probePath);
      signal?.throwIfAborted();
      return probePath;
    },
    async verify(signal) {
      signal?.throwIfAborted();
      try {
        const [direct, tunneled] = await Promise.all([probe(helperPath, signal), probe(probePath, signal)]);
        signal?.throwIfAborted();
        return verifyRouteEvidence(direct, tunneled);
      } catch (error) {
        signal?.throwIfAborted();
        return inconclusive();
      }
    },
  };
}
