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

function normalizedCountry(value: unknown): string {
  const country = typeof value === "string" ? value.trim().toUpperCase() : "";
  return /^[A-Z]{2}$/.test(country) ? country : "";
}

function usableObservations(result: RouteProbeResult | null | undefined): RouteObservation[] {
  if (!result) return [];
  return (Array.isArray(result.observations) ? result.observations : []).filter((item) =>
    Boolean(item && typeof item.source === "string" && item.source && typeof item.ip === "string" && item.ip),
  );
}

export function decideRouteProof(
  direct: RouteProbeResult | null,
  tunneled: RouteProbeResult,
): RouteProofDecision {
  if (!tunneled.success) return { verified: false, reason: "probe_failed" };
  if (!tunneled.discordOk) return { verified: false, reason: "discord_failed" };

  const tunnelObservations = usableObservations(tunneled);
  if (tunnelObservations.length === 0) return { verified: false, reason: "inconclusive" };
  const brazil = tunnelObservations.find((item) => normalizedCountry(item.country) === "BR");
  if (brazil) return { verified: false, reason: "brazil", source: brazil.source, country: "BR" };

  const directBySource = new Map(usableObservations(direct).map((item) => [item.source, item.ip]));
  if (directBySource.size > 0) {
    const comparisons: Array<{ item: RouteObservation; changed: boolean }> = [];
    for (const item of tunnelObservations) {
      const directIP = directBySource.get(item.source);
      if (!directIP) continue;
      comparisons.push({ item, changed: directIP !== item.ip });
    }
    // Uma unica fonte que permaneceu no IP direto basta para caracterizar rota
    // dividida/vazamento. A ordem das respostas paralelas nunca pode decidir o
    // veredito.
    if (comparisons.some((comparison) => !comparison.changed)) {
      return { verified: false, reason: "same_as_direct" };
    }
    const changed = comparisons[0];
    if (changed) {
      return {
        verified: true,
        reason: "verified",
        source: comparisons.map((comparison) => comparison.item.source).join("+"),
        country: normalizedCountry(changed.item.country) || undefined,
      };
    }
    return { verified: false, reason: "inconclusive" };
  }

  // Sem baseline, duas fontes independentes precisam concordar no mesmo IP e
  // pelo menos uma delas precisa informar explicitamente um pais nao brasileiro.
  const byIP = new Map<string, RouteObservation[]>();
  for (const item of tunnelObservations) {
    const entries = byIP.get(item.ip) ?? [];
    if (!entries.some((entry) => entry.source === item.source)) entries.push(item);
    byIP.set(item.ip, entries);
  }
  for (const entries of byIP.values()) {
    const country = entries.map((item) => normalizedCountry(item.country)).find(Boolean);
    if (entries.length >= 2 && country && country !== "BR") {
      return { verified: true, reason: "verified", source: entries.map((item) => item.source).join("+"), country };
    }
  }
  return { verified: false, reason: "inconclusive" };
}

export function maskedIP(value: string | undefined): string {
  if (!value) return "?";
  if (value.includes(":")) {
    const groups = value.split(":").filter(Boolean);
    return `${groups.slice(0, 2).join(":")}:…`;
  }
  const parts = value.split(".");
  return parts.length === 4 ? `${parts[0]}.${parts[1]}.x.x` : "?";
}
