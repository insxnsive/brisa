# Build Brisa

Use Windows x64 with the .NET 10 SDK, Node 22, Python 3.11+ and Go 1.26.6+. Go can download the toolchain specified in `go.mod` when automatic toolchain selection is enabled.

```powershell
git clone https://github.com/insxnsive/brisa.git
cd brisa
npm ci --prefix backend
dotnet tool restore
python packaging/test.py
python packaging/package.py
```

Artifacts are written to `artifacts/releases/`. Packaging builds the Node coordinator and Proton helper, publishes a self-contained WPF app, collects license notices and source, then creates the installer and portable ZIP with Velopack 1.2.158.

The Node distribution's `LICENSE` must sit beside `node.exe` when packaging. Use the official Windows Node distribution rather than copying the executable on its own.

## Layout

- `src/Brisa`: WPF interface, IPC client, verification and updates.
- `backend`: Node coordinator and the Windows networking modules extracted from GoLiveBypass.
- `tools/proton-confgen`: repaired Go helper and its tests.
- `tests`: native lifecycle, appearance, transport and packaging checks.
- `packaging`: build scripts and release validation.

The repository intentionally has no Electron app, Vencord/Equicord plugin, web frontend or Linux/macOS application.

## Tests

`python packaging/test.py` runs the offline regression suites. Backend tests use fixtures; they don't log in, alter a network driver or change an active tunnel.

To inspect the compiled UI without starting the real backend:

```powershell
artifacts/staging/Brisa.exe --ui-test
```

The title explicitly marks test mode. `--smoke-test <output.png> --theme=Dark` captures the retained surfaces and exits; `Light` covers the other theme. Both modes disable update checks and use temporary settings.

These checks don't prove a real Proton account can sign in or that traffic takes the intended route. Live acceptance must be user-initiated, on a machine where changing the tunnel is safe.

After packaging, `python packaging/update_smoke.py` checks a real update between two disposable portable copies, including a rendered launch before and after. It never installs a driver or touches a live account. Setup.exe is checked without installing it.

## Source releases

`source.zip` inside the package contains the source and build scripts for that release, plus the Go helper's vendored dependencies. It excludes build output, local account data and credentials. The same archive is available as a separate release asset.

See [release instructions](releasing.md) before publishing a version. After publication, check the public feed without GitHub credentials:

```powershell
dotnet run --project tests/Brisa.Update.Integration.Tests -c Release -- github 0.0.1 0.1.0-beta.1 artifacts/public-update-check
```

Use the actual target version in that command. It downloads and verifies the update without applying it or opening the live app.
