# macOS Userspace Transport Implementation Plan

> **For Hermes:** Execute with bounded Jev-routed implementation and an independent review. Preserve the safety boundaries in `AGENTS.md`.

**Goal:** Implement and exercise a reusable TCP/UDP WireGuard transport without creating a host VPN interface or enabling Connect.

**Architecture:** A new Go package owns a WireGuard device, its userspace IP stack, pending dials, and returned connections. A separate diagnostic command proves actual encrypted traffic between fresh loopback peers; it accepts no account, configuration-file, or remote-endpoint input. It is a transport milestone, not a macOS routing implementation.

**Tech Stack:** Existing pinned wireguard-go/gVisor dependencies, Go tests, Python packaging contracts, native arm64/x86_64 Mac CI.

## Non-negotiable boundaries

- Never inspect real sessions, credentials, WireGuard configuration, existing VPN state or installed applications.
- No host TUN/utun, routing/DNS changes, app interception, network-driver installation, login, release, tag or version bump.
- Only synthetic keys and loopback WireGuard peers in tests. No public VPN endpoints. No host-network fallback for forwarded traffic or name resolution.
- Keep Windows entry points and behavior unchanged. Keep SwiftUI Connect disabled.
- Do not claim OS-level app isolation, production DNS coverage, live Proton compatibility or signed extension acceptance from a transport test.

## 1. Strict transport configuration (worker)

Create `tools/proton-confgen/internal/macengine/`.
Write and run a failing validation test, implement the minimum, then add one behavior at a time. Validate keys, numeric peer endpoints, supported addresses/MTU, and protocols. Errors must never contain keys or raw configuration. Reject unsupported address families rather than falling back to the host network.

Run from `tools/proton-confgen`:

```sh
go test -timeout 60s ./internal/macengine
```

## 2. Real transport and owned shutdown (worker)

Add context-aware TCP/UDP dialing through the userspace stack. Close must win atomically over late dial publication, cancel pending dials, close active connections, join work, and be safe for concurrent/repeated callers. Prove encrypted TCP and UDP payload delivery with disposable loopback peers, plus cancellation, close-during-read/dial, invalid protocol, and no host fallback. Use explicit coordination and bounded waits rather than timing-only assertions.

## 3. Explicit self-test executable (worker)

Create `tools/proton-confgen/cmd/brisa-tunnel-check/`.
Only `--self-test` runs the disposable loopback diagnostic; other invocations must not create resources. No file/env configuration input or configurable remote peer. The diagnostic returns one strict JSON object after real TCP, UDP, in-tunnel DNS and shutdown checks:

```json
{"schemaVersion":1,"scope":"loopback-only","tcp":true,"udp":true,"dns":true,"shutdown":true}
```

Any failed check exits nonzero. All work must be timeout-bounded; no secrets, packets or raw configs in output.

## 4. Package and execute the exact binary (parent)

Add a failing Python packaging contract, then build/sign/architecture-check/deployment-target-check the diagnostic alongside the existing helper. Add `macos/scripts/transport_acceptance.py` with behavioral tests for invalid output, failures, timeouts and strict schema. Execute the packaged diagnostic, record its SHA-256 and scope-limited evidence, and upload results even on failure. Preserve matching vendored source. Do not invoke the diagnostic from normal app startup.

## 5. Verify and review (parent)

- Run local Go tests and vet, Python tests, diff checks and the actual diagnostic CLI.
- Run an independent Jev-routed review of the bounded new surface; correct reproduced defects with failing tests first.
- Push development commits to `macos` only. Read back the remote SHA.
- Require both native architectures to pass Go race tests, exact packaged diagnostic acceptance and existing native UI/account/package checks.
- Download the exact CI artifacts, verify hashes/source/binary architecture and macOS minimum targets, and inspect recorded acceptance evidence.

## Follow-up, not silently included

The app interception/provider choice, Apple entitlement/signing path, verified source-app identity, network-change recovery, dual-stack and DNS policy, fail-closed routing, real account sign-in and physical-Mac acceptance remain separate gates before Connect can be enabled.

Apple's platform-specific per-app manager API and broader MDM guidance need native validation before choosing the final deployment path. Do not infer unmanaged deployment support from compilation alone or substitute system-wide routing for app-specific routing.

Primary references:
- https://developer.apple.com/documentation/networkextension/netunnelprovidermanager/forperappvpn()
- https://developer.apple.com/documentation/networkextension/netunnelprovidermanager
- https://developer.apple.com/documentation/technotes/tn3120-expected-use-cases-for-network-extension-packet-tunnel-providers
- https://developer.apple.com/documentation/technotes/tn3134-network-extension-provider-deployment
- https://git.zx2c4.com/wireguard-go/tree/tun/netstack/examples/http_client.go
