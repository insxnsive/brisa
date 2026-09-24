import { createProductionBackend } from "./production.ts";
import { startNdjsonServer } from "./server.mjs";

try {
  startNdjsonServer(process.stdin, process.stdout, createProductionBackend());
} catch {
  process.stdout.write(JSON.stringify({ id: "", ok: false, result: {}, error: "Backend initialization failed safely." }) + "\n");
  process.exitCode = 1;
}
