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
- [ ] Obtain native race/packaged evidence on Apple Silicon and Intel and verify downloaded artifacts. Parent owns development commits and CI verification; workers do not commit or publish. No release is authorized.

## Commands

```sh
go test -count=1 -timeout 60s ./internal/macengine ./cmd/brisa-tunnel-check
go vet ./internal/macengine ./cmd/brisa-tunnel-check
python -m unittest discover -s macos/Tests -v
```

Go commands run from `tools/proton-confgen`. Native race evidence comes from macOS CI. Read only source and fixtures; never inspect real account/session/config data, activate/install a network provider, or change the live Windows tunnel.
