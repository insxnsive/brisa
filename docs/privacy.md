# Privacy and local data

This is a source-and-fixture audit, not a claim that live accounts or an installed user's files were inspected.

## Stored data

- The Windows Go helper stores the Proton account identity and session tokens in a current-user DPAPI-protected payload. Session access uses a lock and atomic replacement.
- Passwords, two-factor codes and human-verification answers reach the helper through stdin, not command-line arguments. The username and managed file paths can still appear in helper arguments.
- WireSock requires plaintext WireGuard configuration. Generated, imported and native-owned profiles therefore contain private key material on disk; they are **not encrypted at rest**.
- Native state belongs in `%LOCALAPPDATA%\Brisa\native-data`, separate from the installer payload and non-secret `settings.json`. Brisa creates a private new store and checks existing managed descendants before constructing its helper/network dependencies. It rejects symlinks/junctions, missing DACLs, untrusted owners, and allow entries for principals other than the current user, SYSTEM and Administrators. Inherited permissions are checked too. Existing unsafe stores are rejected, not recursively hardened.
- An older preview's five recognized state/session/profile filenames are migrated as opaque bytes, without decrypting or logging their contents. Migration requires a reliable inactive WireSock inspection; unknown or active state is deferred. Existing conflicting destinations and failed publication preserve the originals. Installer files and unrelated profiles are not migrated or permission-rewritten.
- This is a startup check, not continuous monitoring. It does not defend against an administrator, malware running as the same user, or permission changes after validation. POSIX `0600` mode bits alone are not Windows ACL protection.

## Sign-out and cleanup

Sign-out removes the owned Proton session and lock, generated Proton profile, and native runtime profile. It refuses to remove an active owned tunnel's profile before disconnection. An explicitly imported custom profile is intentionally retained: signing out of Proton is not deletion of a separately imported WireGuard key.

Interrupted operations attempt to remove their staging files. A process crash can leave temporary files; they remain within the checked data directory. Do not attach the directory or any `.conf`/session file to an issue.

## Errors and diagnostics

GUI failures use fixed messages selected from recognized error codes rather than arbitrary helper output. Diagnostics omit untrusted strings such as stderr, paths, account names and token URLs. Structured numeric status and boolean fields remain available; sensitive keys are suppressed regardless of value type. The original developer console sink is not a sanitized export.

A Proton challenge URL intentionally reaches the embedded browser so verification can work. It is restricted to an approved HTTPS Proton host and explicit challenge result. Treat its query as sensitive and do not copy it into diagnostics or reports.

## Verification

Synthetic tests exercise helper-error redaction, challenge handling, console-field suppression, sign-out ownership and imported-profile retention. Disposable Windows NTFS fixtures verify a private parent/profile, rejection of Everyone/Users grants and a junction. Pure decision tests additionally cover unknown ownership and null DACLs. No real credentials, session contents or live VPN profiles were read. See [release verification](verification.md) for the tested version and remaining acceptance boundaries.
