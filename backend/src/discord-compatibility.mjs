// These messages are application-owned. Never forward filesystem/process error
// prose: it can contain private paths or unrelated runtime information.
const messages = Object.freeze({
  DISCORD_UNAVAILABLE: "Brisa could not find a usable Discord installation. Install or update Discord, open it once to finish setup, then try again.",
  DISCORD_CHECK_FAILED: "Brisa could not verify the installed Discord client. Finish any Discord update, restart Discord, then try again.",
  DISCORD_CHANGED: "The selected Discord installation changed or is still updating. Finish updating Discord, open it once, then try again.",
});

export class DiscordCompatibilityError extends Error {
  constructor(code) {
    const safeCode = Object.hasOwn(messages, code) ? code : "DISCORD_CHECK_FAILED";
    super(messages[safeCode]);
    this.code = safeCode;
  }
}

// Starting or relaunching requires the current executable. Cleanup may still
// target an earlier verified selection; the lifecycle layer independently checks
// live process paths/PIDs before stopping anything. Never trust an arbitrary path.
export function createDiscordSelectionGuard(discover) {
  const verified = new Set();
  const selected = apps => {
    if (!Array.isArray(apps) || !apps.length || apps.some(app => typeof app !== "string" || !app))
      throw new DiscordCompatibilityError("DISCORD_CHANGED");
    return apps.map(app => app.toLowerCase());
  };
  const assertCurrent = apps => {
    const keys = selected(apps);
    const current = new Set(discover().map(app => app.toLowerCase()));
    if (keys.some(app => !current.has(app))) throw new DiscordCompatibilityError("DISCORD_CHANGED");
    keys.forEach(app => verified.add(app));
  };
  return {
    assertCurrent,
    assertCleanup(apps) {
      if (selected(apps).every(app => verified.has(app))) return;
      assertCurrent(apps);
    },
  };
}

export function discordCompatibilityFailure(error) {
  const code = error instanceof DiscordCompatibilityError && Object.hasOwn(messages, error.code)
    ? error.code : "DISCORD_CHECK_FAILED";
  return { success: false, code, message: messages[code] };
}
