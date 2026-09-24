import type { ProtonHumanVerificationMethod } from "./proton-captcha";

export type ProtonCertificateErrorCode = "CAPTCHA_REQUIRED" | "CAPTCHA_INVALID" | "CAPTCHA_CANCELLED";

export type ProtonCertificateResult = {
  success: boolean;
  code?: string;
  error?: string;
  message?: string;
  retryable?: boolean;
  captchaUrl?: string;
  [key: string]: unknown;
};

export type ProtonCaptchaSolveResult =
  | { ok: true; token: string; method?: ProtonHumanVerificationMethod }
  | { ok: false; code: "CAPTCHA_CANCELLED" | "CAPTCHA_INVALID"; message: string };

export function normalizeProtonCertificateFailure(
  value: unknown,
  fallbackError: string,
): ProtonCertificateResult {
  const fields = value && typeof value === "object" ? value as Record<string, unknown> : {};
  const code = fields.code === "CAPTCHA_REQUIRED" || fields.code === "CAPTCHA_INVALID"
    ? fields.code
    : undefined;
  const error = typeof fields.error === "string" && fields.error.trim()
    ? fields.error.trim()
    : fallbackError;
  if (!code) return { success: false, error };

  const captchaUrl = typeof fields.captchaUrl === "string" && fields.captchaUrl.length <= 8192
    ? fields.captchaUrl
    : undefined;
  return {
    success: false,
    code,
    error,
    retryable: fields.retryable !== false,
    ...(captchaUrl ? { captchaUrl } : {}),
  };
}

function cancelledResult(message: string): ProtonCertificateResult {
  return {
    success: false,
    code: "CAPTCHA_CANCELLED",
    retryable: true,
    message,
    error: message,
  };
}

export async function runProtonCertificateOperation<T extends ProtonCertificateResult>(
  run: (humanVerificationToken?: string, humanVerificationMethod?: ProtonHumanVerificationMethod) => Promise<T>,
  solve: (captchaUrl: string, signal?: AbortSignal) => Promise<ProtonCaptchaSolveResult>,
  options: {
    signal?: AbortSignal;
    isCurrent?: () => boolean;
    onStatus?: (status: "opening" | "retrying" | "verifying") => void;
    maxChallenges?: number;
  } = {},
): Promise<T | ProtonCertificateResult> {
  const current = () => !options.signal?.aborted && (options.isCurrent?.() ?? true);
  if (!current()) return cancelledResult("A operação Proton foi cancelada antes de iniciar.");

  let result = await run(undefined);
  if (!current()) {
    return cancelledResult("A verificação foi cancelada porque a conta ou a operação mudou.");
  }
  const maxChallenges = Math.max(0, Math.min(3, options.maxChallenges ?? 3));
  for (let attempt = 1; !result.success &&
    (result.code === "CAPTCHA_REQUIRED" || result.code === "CAPTCHA_INVALID") &&
    attempt <= maxChallenges; attempt++) {
    if (!result.captchaUrl || !current()) {
      return current() ? result : cancelledResult("A verificação foi cancelada porque a conta ou a operação mudou.");
    }
    options.onStatus?.(attempt === 1 ? "opening" : "retrying");
    const solved = await solve(result.captchaUrl, options.signal);
    if (!solved.ok) {
      return {
        success: false,
        code: solved.code,
        retryable: true,
        message: solved.message,
        error: solved.message,
      };
    }
    if (!current()) {
      return cancelledResult("A verificação foi cancelada porque a conta ou a operação mudou.");
    }
    options.onStatus?.("verifying");
    result = solved.method ? await run(solved.token, solved.method) : await run(solved.token);
    if (!current()) {
      return cancelledResult("A verificação foi cancelada porque a conta ou a operação mudou.");
    }
  }
  return result;
}
