const MAX_FRAME_BYTES = 1024 * 1024;
const PUBLIC_ERRORS = new Set([
  "Payload must be an object.", "Payload schema is invalid.", "Unknown command.",
  "A human-verification method is required with its token.",
]);

function response(id, ok, result, error) {
  return JSON.stringify({ id, ok, result, ...(error ? { error } : {}) }) + "\n";
}

function validRequest(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return false;
  if (Object.keys(value).some(key => !["id", "command", "payload"].includes(key)) || Object.keys(value).length !== 3) return false;
  return typeof value.id === "string" && value.id.length > 0 && value.id.length <= 128 && !/[\r\n\0]/.test(value.id) &&
    typeof value.command === "string" && value.command.length > 0 && value.command.length <= 64 &&
    value.payload !== null && typeof value.payload === "object" && !Array.isArray(value.payload);
}

export function startNdjsonServer(input, output, backend) {
  let buffer = Buffer.alloc(0);
  let discarding = false;
  const write = value => { try { output.write(value); } catch {} };
  const handle = async line => {
    let request;
    try { request = JSON.parse(line.toString("utf8")); }
    catch { write(response("", false, {}, "Invalid JSON frame.")); return; }
    if (!validRequest(request)) { write(response(typeof request?.id === "string" ? request.id.slice(0, 128) : "", false, {}, "Invalid request schema.")); return; }
    try {
      const result = await backend.execute(request.command, request.payload);
      write(response(request.id, true, result));
    } catch (error) {
      const message = error instanceof Error && PUBLIC_ERRORS.has(error.message) ? error.message : "Command failed safely.";
      write(response(request.id, false, {}, message));
    }
  };
  input.on("data", chunk => {
    const data = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    let offset = 0;
    while (offset < data.length) {
      const newline = data.indexOf(0x0a, offset);
      const end = newline === -1 ? data.length : newline;
      const part = data.subarray(offset, end);
      if (!discarding) {
        if (buffer.length + part.length > MAX_FRAME_BYTES) {
          buffer = Buffer.alloc(0);
          discarding = true;
        } else buffer = Buffer.concat([buffer, part]);
      }
      if (newline !== -1) {
        if (discarding) write(response("", false, {}, "Frame exceeds maximum size."));
        else if (buffer.length) void handle(buffer);
        else write(response("", false, {}, "Invalid JSON frame."));
        buffer = Buffer.alloc(0);
        discarding = false;
        offset = newline + 1;
      } else offset = data.length;
    }
  });
  input.on("end", () => {
    if (discarding) write(response("", false, {}, "Frame exceeds maximum size."));
    else if (buffer.length) write(response("", false, {}, "Incomplete JSON frame."));
  });
}
