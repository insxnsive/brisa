import type { ProtonHumanVerificationMethod } from "./proton-captcha";

export type ProtonCertificateErrorCode =
  | "CAPTCHA_REQUIRED"
  | "CAPTCHA_INVALID"
  | "CAPTCHA_CANCELLED"
  | "APP_VERSION_UNSUPPORTED";

export const APP_VERSION_UNSUPPORTED_RE =
  /app_version_unsupported|with code: 50(?:01|03|05)\b|no longer (?:supported|accepts)/i;

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
  const error = typeof fields.error === "string" && fields.error.trim()
    ? fields.error.trim()
    : fallbackError;
  if (fields.code === "APP_VERSION_UNSUPPORTED") {
    return { success: false, code: "APP_VERSION_UNSUPPORTED", error, retryable: false };
  }
  const code = fields.code === "CAPTCHA_REQUIRED" || fields.code === "CAPTCHA_INVALID"
    ? fields.code
    : undefined;
  if (!code) {
    const raw = `${typeof fields.error === "string" ? fields.error : ""} ${fallbackError}`;
    if (APP_VERSION_UNSUPPORTED_RE.test(raw)) {
      return { success: false, code: "APP_VERSION_UNSUPPORTED", error, retryable: false };
    }
    return { success: false, error };
  }

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

