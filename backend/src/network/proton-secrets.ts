const secretFlags: Record<string, string> = {
  "-password": "password",
  "-2fa": "twoFactorCode",
  "-hv-token": "humanVerificationToken",
  "-hv-method": "humanVerificationMethod",
};

/** Keep secrets out of OS process listings as well as the application's logs. */
export function confgenSecretTransport(input: readonly string[]): { args: string[]; stdin: string | undefined } {
  const args: string[] = [];
  const secrets: Record<string, string> = {};
  for (let i = 0; i < input.length; i++) {
    const key = secretFlags[input[i]];
    if (key) {
      if (i + 1 >= input.length) throw new Error("Missing Proton secret argument.");
      secrets[key] = input[++i];
    } else {
      args.push(input[i]);
    }
  }
  if (secrets.humanVerificationToken && !secrets.humanVerificationMethod) {
    secrets.humanVerificationMethod = "captcha";
  }
  if (!Object.keys(secrets).length) return { args, stdin: undefined };
  args.push("-stdin-secrets");
  return { args, stdin: `${JSON.stringify(secrets)}\n` };
}
