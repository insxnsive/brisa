# Release a version

Updates come from `insxnsive/brisa` GitHub Releases, on Velopack's `win` channel. Do not point a build at upstream GoLiveBypass releases.

## Development is not publication

Accumulate fixes in commits and CI artifacts. Do not bump versions, create release tags or publish a release for each patch. A new release is a deliberate maintainer-requested milestone after acceptance, not the default end of a coding task. Branch pushes only build development artifacts.

`CHANGELOG.md` retains the complete history. Each GitHub release body contains only the exact version's section; `packaging/release.py` extracts it and rejects missing, duplicate or empty sections. Do not pass the complete changelog as release notes or append previous versions' patches. This notes-only change does not require a new app release.

## Publish an approved version

1. Change `<Version>` in `src/Brisa/Brisa.csproj` and update `CHANGELOG.md`.
2. Run `python packaging/test.py` and inspect the app in isolated UI mode. Packaging also runs `packaging/production_startup_smoke.py` against the real bundled backend from the app working directory using disposable storage. This production-startup gate is required in addition to, not replaced by, `--ui-test`.
3. Commit the implementation. If `scripts/install.ps1` changed, update both README bootstrap blocks to that full commit ID and the exact script SHA-256. Run the bootstrap fixtures and commit the README before tagging.
4. Tag the final source commit as `v<version>`, then push the branch and tag. The release workflow validates the tag/version, runs the offline suites and builds the Windows artifacts.
5. Check the workflow and release assets before sharing the download. Never restore mutable branch piping or equate a checksum with signing.

Use numbered prereleases such as `0.1.0-beta.1` while testing. Prerelease tags must produce GitHub prereleases, not a stable latest release. A source commit alone does not update installed copies.

Every release needs the Velopack installer, portable ZIP, full update package, `releases.win.json`, matching source archive and `SHA256SUMS`. Keep the generated package filenames and feed together. The app uses the feed's package checksums; don't replace one asset with bytes from another build.

Never upload account/session files, WireGuard profiles or signing credentials. GitHub credentials belong only in the release job, not in the app. The app downloads public assets without a token.

The initial releases are unsigned. An Authenticode signing certificate can be added later through Velopack's signing options. Do not describe checksum validation as publisher signing.

Before a stable release, obtain written WireSock production-use clarification, add verified publisher signing, and complete [clean-machine acceptance](clean-machine-acceptance.md). The maintainer has accepted the current live connection/routing path; do not repeat or disturb it merely to refresh a checklist. Keep that user-observed acceptance separate from offline fixtures, UI captures, portable updates, and still-unperformed install/reboot/uninstall checks.
