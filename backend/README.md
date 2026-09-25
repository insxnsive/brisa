# Brisa backend

Private Node child process for the native Windows host. It accepts one NDJSON request per line on stdin and emits one response per line on stdout. Payloads and raw helper output are never logged or echoed. State is isolated under `%LOCALAPPDATA%\\Brisa` unless the host supplies an absolute `BRISA_DATA_DIR`.

## Build and test

```powershell
npm.cmd test
npm.cmd run build
```

Run `npm.cmd ci` first. The exact local esbuild version is pinned in `package-lock.json`. The build creates `dist/backend.cjs`; the test command additionally creates `dist/backend-fixture.cjs`, which has injected fake dependencies and is not referenced by the production bundle.

## Packaged layout

The host starts `runtime/node.exe backend/backend.cjs` with the application directory as cwd and sets:

- `BRISA_DATA_DIR` to an absolute isolated data directory.
- `BRISA_RESOURCE_DIR` to the absolute packaged `resources` directory.
- `BRISA_DISCORD_EXE` optionally to an absolute, explicitly user-selected supported Discord executable when automatic safe discovery is insufficient.

The Proton helper is packaged at `resources/extra/proton-confgen/proton-confgen.exe`. WireSock is not bundled; if no compatible SDK is found when the user starts a connection, the backend downloads the pinned x64 installer from the official WireSock endpoint, checks its SHA-256, and launches its visible installer with a Windows elevation prompt. The user must accept WireSock's installer/license prompts. Production never falls back to fixture data.

## Safety boundary

The owned WireSock config is exactly `BRISA_DATA_DIR/native-wiresock.conf`. Any active WireSock using another config is external to this backend. External or unreliable inspection blocks every mutating command; disconnect stops only the exact native-owned config. The backend never performs global recovery, uninstall, or takeover.
