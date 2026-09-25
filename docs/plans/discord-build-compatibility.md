# Discord Build Compatibility Implementation Plan

**Goal:** Address the reported `app-1.0.9259` / `app-1.0.9258` mismatch without imposing an invented minimum Discord version.

**Architecture:** Discover valid installed clients at operation time, replace stale profile app filters before starting, and return fixed, actionable compatibility messages before any Discord/tunnel mutation. Keep compatibility based on verifiable installation layout and executable metadata, not a release-number guess.

**Tech stack:** Existing Node/TypeScript backend, Node test fixtures, WPF packaged acceptance.

## Report and boundaries

- Report: a friend's older Discord installation failed while configuration referred to `app-1.0.9259`.
- Source inspection found no literal `9259` requirement. Discovery already sorts installed `app-*` directories, and WireSock start rewrites `AllowedApps`.
- The friend's exact failure is not yet reproduced. Do not claim this report proved a hardcoded source constant or that `1.0.9258` is unsupported.
- Investigate stale saved filters, incomplete higher-version folders, and updates between discovery and use.
- Never read private runtime configuration/account files; use disposable synthetic profiles and installations. Never touch a live app, driver or tunnel. No release/tag/version bump.

## Tracked work

- [x] Reproduce `1.0.9258` discovery and replacement of a synthetic `1.0.9259` filter.
- [x] Add a failing fixture for an incomplete newer build shadowing a usable older build; fix discovery if reproduced.
- [x] Fail safely with install/update/repair guidance when no usable client can be resolved, without calling profile generation, Discord stop or WireSock start.
- [x] Revalidate selected executables immediately before side effects and give update-in-progress guidance rather than a generic error.
- [x] Exercise the actual WPF Connect control through the bundled synthetic coordinator in Dark/Light disposable desktops; verify the complete warning and return to Connect.
- [x] Run all offline suites and independent review. Repair the review-discovered update race: previously verified selections remain trusted only for rollback/disconnect, not fresh startup or relaunch. PID/path ownership checks remain unchanged.
- [x] Verify the development Windows CI package: [run 36194382660](https://github.com/insxnsive/brisa/actions/runs/36194382660), code commit `df91282e140f158121b0690416c1581722ab3754`. All 95 backend tests passed in CI, plus packaging, Go, WPF, production-startup and disposable portable-update checks. Downloaded hashes, package manifest, source commit and all changed source files matched. No production installation or live routing test was performed.
- [ ] Friend-machine confirmation remains outstanding; no unsupported minimum version is established.

## Verification commands

```powershell
node --experimental-strip-types --test backend/tests/windows-discord-install.test.mjs
node --test backend/tests/backend.test.mjs
python packaging/test.py
```

Use RED -> GREEN slices, then one integration review and the full offline gate. Use the existing isolated desktop/packaged fixture path for UI acceptance, never the user's desktop application.
