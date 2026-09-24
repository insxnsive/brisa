export const PROTON_CAPTCHA_IPC_CHANNEL = "proton-captcha-response";

export type ProtonHumanVerificationMethod = "captcha" | "ownership-email" | "ownership-sms";

const SUPPORTED_METHODS = new Set<ProtonHumanVerificationMethod>([
  "captcha",
  "ownership-email",
  "ownership-sms",
]);
const MAX_TOKEN_LENGTH = 16_384;
const MAX_MESSAGE_LENGTH = 32_768;

export interface ProtonCaptchaChallenge {
  url: string;
  origin: string;
  challenge: string;
  methods: readonly ProtonHumanVerificationMethod[];
}

function isOfficialProtonHost(hostname: string): boolean {
  const normalized = hostname.toLowerCase();
  return normalized === "proton.me" || normalized.endsWith(".proton.me");
}

export function parseProtonCaptchaChallenge(rawUrl: string): ProtonCaptchaChallenge | null {
  try {
    const parsed = new URL(rawUrl);
    if (parsed.protocol !== "https:" || parsed.username || parsed.password || parsed.port || parsed.hash) return null;

    if (parsed.hostname.toLowerCase() === "verify.proton.me" && parsed.pathname === "/") {
      const allowedKeys = new Set(["token", "methods", "embed", "vpn"]);
      if ([...parsed.searchParams.keys()].some((key) => !allowedKeys.has(key))) return null;
      if (["token", "methods", "embed", "vpn"].some((key) => parsed.searchParams.getAll(key).length !== 1)) return null;
      const rawChallenge = parsed.searchParams.get("token") ?? "";
      const challenge = rawChallenge.trim();
      const offered = (parsed.searchParams.get("methods") ?? "").split(",");
      if (challenge !== rawChallenge || challenge.length < 3 || challenge.length > 4096 || parsed.searchParams.get("embed") !== "1" || parsed.searchParams.get("vpn") !== "1") return null;
      if (!offered.length || offered.some((method) => !SUPPORTED_METHODS.has(method as ProtonHumanVerificationMethod))) return null;
      const methods = [...new Set(offered)] as ProtonHumanVerificationMethod[];
      if (methods.length !== offered.length) return null;
      return { url: parsed.toString(), origin: parsed.origin, challenge, methods };
    }

    const rawChallenge = parsed.searchParams.get("Token") ?? "";
    const challenge = rawChallenge.trim();
    if (!isOfficialProtonHost(parsed.hostname) || parsed.pathname !== "/core/v4/captcha") return null;
    if (parsed.searchParams.getAll("Token").length !== 1 || [...parsed.searchParams.keys()].some((key) => key !== "Token")) return null;
    if (challenge !== rawChallenge || challenge.length < 3 || challenge.length > 4096) return null;
    return { url: parsed.toString(), origin: parsed.origin, challenge, methods: ["captcha"] };
  } catch {
    return null;
  }
}

export function isAllowedProtonCaptchaNavigation(rawUrl: string, expected: ProtonCaptchaChallenge): boolean {
  try {
    const parsed = new URL(rawUrl);
    return !parsed.username && !parsed.password && !parsed.port && !parsed.hash &&
      parsed.origin === expected.origin && parsed.toString() === expected.url;
  } catch {
    return false;
  }
}

export function validateProtonCaptchaResponse(token: unknown, expectedChallenge: string): token is string {
  if (typeof token !== "string" || token.length > MAX_TOKEN_LENGTH) return false;
  const prefix = `${expectedChallenge}:`;
  return token.startsWith(prefix) && token.length > prefix.length;
}

export interface ProtonVerificationMessage {
  type: "pm_captcha" | "proton_captcha" | "HUMAN_VERIFICATION_SUCCESS";
  token: string;
  method: ProtonHumanVerificationMethod;
}

export function parseProtonVerificationMessage(input: unknown): ProtonVerificationMessage | null {
  let data = input;
  if (typeof data === "string") {
    if (!data.length || data.length > MAX_MESSAGE_LENGTH) return null;
    try { data = JSON.parse(data); } catch { return null; }
  }
  if (!data || typeof data !== "object") return null;
  const fields = data as Record<string, unknown>;
  if (fields.type === "HUMAN_VERIFICATION_SUCCESS") {
    if (!fields.payload || typeof fields.payload !== "object") return null;
    const payload = fields.payload as Record<string, unknown>;
    if (typeof payload.token !== "string" || !payload.token.length || payload.token.length > MAX_TOKEN_LENGTH) return null;
    if (!SUPPORTED_METHODS.has(payload.type as ProtonHumanVerificationMethod)) return null;
    return { type: fields.type, token: payload.token, method: payload.type as ProtonHumanVerificationMethod };
  }
  if (fields.type !== "pm_captcha" && fields.type !== "proton_captcha") return null;
  if (typeof fields.token !== "string" || !fields.token.length || fields.token.length > MAX_TOKEN_LENGTH) return null;
  return { type: fields.type, token: fields.token, method: "captcha" };
}

export function validateProtonVerificationResponse(
  token: unknown,
  method: unknown,
  expected: ProtonCaptchaChallenge,
): token is string {
  if (typeof method !== "string" || !expected.methods.includes(method as ProtonHumanVerificationMethod)) return false;
  if (method === "captcha") return validateProtonCaptchaResponse(token, expected.challenge);
  return typeof token === "string" && token.length > 0 && token.length <= MAX_TOKEN_LENGTH;
}
