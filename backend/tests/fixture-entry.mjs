import { createBackend } from "../src/backend.mjs";
import { startNdjsonServer } from "../src/server.mjs";

const files = new Map();
const backend = createBackend({
  inspect: async () => ({ active: false, owned: false, reliable: true, reason: null }),
  getSavedSessionUsername: async () => "fixture-user",
  getPlan: async () => ({ success: false, status: "unknown" }),
  login: async () => ({ success: false }), generate: async () => ({ success: false }),
  start: async () => ({}), stop: async () => ({ stopped: true }),
  validateConfig: () => ({ valid: false }), sanitizeConfig: value => value,
  discoverDiscord: async () => [], launchDiscord: async () => {}, helperAvailable: () => true,
  files: {
    readExplicit: async () => "", readOwned: async p => files.get(p) || "",
    writeOwned: async (p, v) => files.set(p, v), removeOwned: async p => files.delete(p), existsOwned: async p => files.has(p),
  },
}, {
  dataDir: "fixture", statePath: "fixture/state.json", sessionPath: "fixture/proton-session.json",
  ownedConfigPath: "fixture/native-wiresock.conf", importedProfilePath: "fixture/imported.conf", generatedProfilePath: "fixture/wireguard.conf",
});

startNdjsonServer(process.stdin, process.stdout, backend);
