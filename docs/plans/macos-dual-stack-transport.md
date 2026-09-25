# Mac Dual-Stack Transport Implementation Plan

**Goal:** Extend the isolated userspace WireGuard transport to IPv6 without creating host routes, interfaces, resolver changes or an enabled Connect button.

**Architecture:** Explicit typed inner addresses/routes/DNS, family-correct userspace TCP/UDP dialing, in-tunnel A/AAAA DNS only, unchanged joined cancellation/close semantics. Prove both families through the packaged loopback diagnostic and reject family mismatches, scoped/link-local/ambiguous addresses and undeclared routes before dialing. No production VPN or app-isolation claim.

**Tech stack:** Existing Go WireGuard netstack and deterministic loopback fixtures, Python report acceptance, dual-architecture Mac CI.

## Tasks

- [x] Add one failing IPv6 encrypted TCP fixture, implement the minimum typed dual-stack contract, and preserve IPv4 tests.
- [x] Add IPv6 UDP, A/AAAA DNS, explicit-family mismatch, DNS failure/no-host-fallback and cancellation/close coverage using bounded fixture contexts.
- [x] Extend the real packaged diagnostic and strict Python schema/tests so old IPv4-only evidence cannot pass the new milestone.
- [x] Update bilingual limitations in `docs/macos.md` and run focused Go tests/vet and Python tests.
- [x] Obtain independent review and resolve pending-DNS error identity and unjoined self-test workers; verify the fixes and repeat local transport tests 25 times.
- [x] Verify native Apple Silicon and Intel artifacts from [run 36194386622](https://github.com/insxnsive/brisa/actions/runs/36194386622), code commit `0c1f4f626eb2f876b70214c4b34e78047757ddd5`: 12 transport test cases repeated three times per architecture with the race detector, exact packaged version-2 dual-stack diagnostics, native UI/account fixtures, hashes, tracked source, Mach-O architecture and deployment targets. Connect remains disabled; no host routing, real-account acceptance, production signing or release is claimed.

## Commands

```sh
go test -count=1 -timeout 60s ./internal/macengine ./cmd/brisa-tunnel-check
go vet ./internal/macengine ./cmd/brisa-tunnel-check
python -m unittest discover -s macos/Tests -v
```

Go commands run from `tools/proton-confgen`. Native race evidence comes from macOS CI. Read only source and fixtures; never inspect real account/session/config data, activate/install a network provider, or change the live Windows tunnel.
