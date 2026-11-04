export interface RouteObservation {
  source: string;
  ip: string;
  country?: string;
  latencyMs?: number;
}

export interface RouteProbeResult {
  success: boolean;
  observations: RouteObservation[];
  discordOk: boolean;
  discordMs?: number;
  error?: string;
}

export interface RouteProofDecision {
  verified: boolean;
  reason: "verified" | "probe_failed" | "discord_failed" | "brazil" | "same_as_direct" | "inconclusive";
  source?: string;
  country?: string;
}

