import test from "node:test";
import assert from "node:assert/strict";
import { APP_MESSAGES } from "../src/backend.mjs";

// O texto em inglês é o contrato: quem já usa o Brisa em inglês não pode ver uma frase diferente
// depois de as falhas passarem a carregar código. Esta tabela fixa as frases, e o código é o que a
// interface traduz em pt-BR.
const EXPECTED = {
  WIRESOCK_STATE_UNRELIABLE: "WireSock state could not be inspected reliably; no changes were made.",
  TUNNEL_MIXED_OWNERSHIP: "Brisa's tunnel is still running alongside another WireSock tunnel. Close the other tunnel, then disconnect Brisa. Brisa will stay open until its tunnel can be stopped safely.",
  TUNNEL_EXTERNAL_ACTIVE: "Another WireSock tunnel is active; no changes were made.",
  PROTON_HELPER_MISSING: "The packaged Proton helper is unavailable.",
  NOT_SIGNED_IN: "Sign in to ProtonVPN first.",
  MUTATION_BUSY: "Another native backend operation is still running.",
  SIGN_OUT_WITH_TUNNEL: "Disconnect the native tunnel before signing out.",
  ROUTE_CHANGE_ACTIVE: "Disconnect before optimizing; active tunnels are never switched automatically.",
  PROFILE_PATH_NOT_ABSOLUTE: "Select an absolute WireGuard .conf file.",
  PROFILE_UNREADABLE: "The selected WireGuard file could not be read.",
  TUNNEL_ALREADY_ACTIVE: "A Brisa tunnel is already active. Disconnect it before starting a fresh Discord connection.",
  PROFILE_MISSING: "Import a WireGuard profile first.",
  ROUTE_UNVERIFIED: "Discord was reopened and the tunnel is running, but the routed connection could not be verified. Disconnect and try another route.",
  TUNNEL_OWNERSHIP_UNCONFIRMED: "Brisa's tunnel may still be running, but ownership could not be confirmed. Run Brisa as administrator and retry. Brisa will stay open.",
  DISCORD_REOPEN_FAILED: "The tunnel was stopped, but Discord could not be reopened. Open Discord manually.",
  STOP_NOT_CONFIRMED: "The native-owned tunnel could not be stopped safely. Brisa will stay open. Check the tunnel status before retrying.",
  STOP_NOT_CONFIRMED_DISCORD_CLOSED: "Discord was closed, but the native-owned tunnel could not be stopped safely. Brisa will stay open. Check the tunnel status before reopening Discord.",
  STOP_NOT_CONFIRMED_OR_CLOSED: "Discord or the tunnel could not be stopped safely. Close Discord and try disconnecting again.",
  SDK_INSTALL_STAYS_OPEN: "WireSock installation status is unknown. Brisa will stay open; restart Brisa before trying again.",
  SDK_INSTALL_DISCORD_CLOSED: "Discord was closed; open it again manually.",
  CANCEL_RESTORE_UNCONFIRMED: "Cancellation could not confirm normal-route restoration. Check tunnel status and Discord before retrying.",
  SIGN_OUT_PROFILE_KEPT: "Signed out. Brisa kept its native tunnel profile because tunnel ownership could not be verified. Open Brisa as administrator and sign out again to finish clearing that profile.",
};

test("every converted message keeps its exact English text", () => {
  assert.deepEqual(Object.keys(APP_MESSAGES).sort(), Object.keys(EXPECTED).sort());
  for (const [code, english] of Object.entries(EXPECTED)) {
    assert.equal(APP_MESSAGES[code], english, code);
  }
});

test("no converted message is empty, duplicated or shares another code's text", () => {
  const seen = new Map();
  for (const [code, english] of Object.entries(APP_MESSAGES)) {
    assert.ok(code.length > 3, code);
    assert.ok(english.trim().length > 0, code);
    assert.equal(seen.has(english), false, `${code} repeats ${seen.get(english)}`);
    seen.set(english, code);
  }
});
