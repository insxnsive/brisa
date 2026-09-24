# Release a version

Updates come from `insxnsive/brisa` GitHub Releases, on Velopack's `win` channel. Do not point a build at upstream GoLiveBypass releases.

1. Change `<Version>` in `src/Brisa/Brisa.csproj` and update `CHANGELOG.md`.
2. Run `python packaging/test.py` and inspect the app in isolated UI mode.
3. Commit the source and tag that commit as `v<version>`.
4. Push the branch and tag. The release workflow validates the tag/version, runs the offline suites and builds the Windows artifacts.
5. Check the workflow and release assets before sharing the download.

Use numbered prereleases such as `0.1.0-beta.1` while testing. Prerelease tags must produce GitHub prereleases, not a stable latest release. A source commit alone does not update installed copies.

Every release needs the Velopack installer, portable ZIP, full update package, `releases.win.json`, matching source archive and `SHA256SUMS`. Keep the generated package filenames and feed together. The app uses the feed's package checksums; don't replace one asset with bytes from another build.

Never upload account/session files, WireGuard profiles or signing credentials. GitHub credentials belong only in the release job, not in the app. The app downloads public assets without a token.

The initial releases are unsigned. An Authenticode signing certificate can be added later through Velopack's signing options. Do not describe checksum validation as publisher signing.

Before a stable release, complete live native Proton verification and tunnel acceptance on a suitable test machine. Offline regression tests and UI captures are not substitutes.
