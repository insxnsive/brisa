import { createProductionBackend } from "./production.ts";
import { startNdjsonServer } from "./server.mjs";
import { initializationFailureFrame } from "./startup-error.mjs";

try {
  startNdjsonServer(process.stdin, process.stdout, createProductionBackend());
} catch (error) {
  const frame = JSON.stringify(initializationFailureFrame(error)) + "\n";
  process.stdout.write(frame);
  process.stderr.write(frame);
  process.exitCode = 1;
}
