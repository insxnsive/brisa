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

