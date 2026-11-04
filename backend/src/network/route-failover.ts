
export type FailoverHealth = "healthy" | "failed" | "unknown";

export interface FailoverTunnelStats {
  ok: boolean;
  handshakeAgoS: number | null;
  rxBytes: number | null;
  txBytes: number | null;
  error?: string;
}

export interface FailoverWireSockStatus {
  state: "connected" | "connecting" | "disconnected" | "unknown";
  source: "cli" | "service" | "none";
}

export interface FailoverHealthSample {
  discordRunning: boolean;
  tunnelActive: boolean;
  stats?: FailoverTunnelStats | null;
  wireSock?: FailoverWireSockStatus | null;
  trafficIncreasing?: boolean;
  /** A Linux status error naming a missing namespace/interface is evidence of failure. */
  statsFailureEvidence?: boolean;
}

export interface FailoverObservation {
  health: FailoverHealth;
  consecutiveFailures: number;
  trigger: boolean;
  inGracePeriod: boolean;
}

export interface ProtonRouteMetadata {
  success?: boolean;
  server: string;
  country: string;
  city: string;
  tier: string;
  load: number;
  score: number;
  pingMs: number;
  endpoint: string;
  confFile: string;
  expiresAt?: number;
  generatedAt?: string;
}

export interface ProtonRoutePoolManifest {
  version: 1;
  username: string;
  country: string;
  freeOnly: true;
  autoPing: boolean;
  createdAt: string;
  active?: ProtonRouteMetadata;
  reserves: ProtonRouteMetadata[];
  quarantined: string[];
  renewalUsed: boolean;
  disabled?: boolean;
}

