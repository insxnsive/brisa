import assert from "node:assert/strict";
import test from "node:test";
import * as compatibility from "../src/discord-compatibility.mjs";

const old = "C:\\Discord\\app-1.0.9258\\Discord.exe";
const updated = "C:\\Discord\\app-1.0.9259\\Discord.exe";

test("a previously verified Discord selection remains trusted only for cleanup after an update", () => {
  assert.equal(typeof compatibility.createDiscordSelectionGuard, "function", "cleanup needs a separate previously-verified selection guard");
  let current = [old];
  const guard = compatibility.createDiscordSelectionGuard(() => current);
  guard.assertCurrent([old]);
  current = [updated];
  assert.throws(() => guard.assertCurrent([old]), { code: "DISCORD_CHANGED" });
  assert.doesNotThrow(() => guard.assertCleanup([old]));
  assert.throws(() => guard.assertCleanup(["C:\\Unrelated\\Discord.exe"]), { code: "DISCORD_CHANGED" });
  assert.throws(() => guard.assertCleanup([]), { code: "DISCORD_CHANGED" });
});
