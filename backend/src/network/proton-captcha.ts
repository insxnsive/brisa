export type ProtonHumanVerificationMethod = "captcha" | "ownership-email" | "ownership-sms";

export interface ProtonCaptchaChallenge {
  url: string;
  origin: string;
  challenge: string;
  methods: readonly ProtonHumanVerificationMethod[];
}

export interface ProtonVerificationMessage {
  type: "pm_captcha" | "proton_captcha" | "HUMAN_VERIFICATION_SUCCESS";
  token: string;
  method: ProtonHumanVerificationMethod;
}

