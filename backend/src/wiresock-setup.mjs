// Application-owned setup guidance. Never forward vendor/process error prose.
export const WIRESOCK_SETUP_GUIDANCE = "Brisa requires WireSock Secure Connect SDK for Windows x64 (3.4.8.1 or a compatible newer SDK). Download the SDK from https://wiresock.net/wiresock-sdk. The standalone WireSock Secure Connect GUI is not the SDK Brisa needs. Review the vendor terms and approve the Windows installation prompt, then retry in Brisa.";

export class WireSockSetupError extends Error {
  constructor(cancelled = false) {
    super(WIRESOCK_SETUP_GUIDANCE);
    this.cancelled = cancelled === true;
  }
}

export function wireSockSetupFailure(error) {
  return { success: false, code: "WIRESOCK_SETUP_REQUIRED",
    message: `${error.cancelled === true ? "WireSock setup was cancelled." : "WireSock SDK setup could not be completed."} ${WIRESOCK_SETUP_GUIDANCE}` };
}
