package vpn

import (
	"encoding/base64"
	"fmt"
	"testing"

	"protonvpn-wg-confgen/internal/api"
	"protonvpn-wg-confgen/internal/config"
	"protonvpn-wg-confgen/internal/constants"
)

const (
	nlServer = "NL#1"
	chServer = "CH#1"
	usServer = "US#1"
)

// server builds an online logical server with one online physical server.
func server(country string, tier, features int) api.LogicalServer {
	return api.LogicalServer{
		Name:        country + "#1",
		ExitCountry: country,
		Tier:        tier,
		Features:    features,
		Status:      constants.StatusOnline,
		Servers:     []api.PhysicalServer{{Status: constants.StatusOnline}},
	}
}

func TestEligibleServers(t *testing.T) {
	var (
		plusP2P    = server("NL", api.TierPlus, api.FeatureP2P)
		plusPlain  = server("NL", api.TierPlus, 0)
		plusCore   = server("CH", api.TierPlus, api.FeatureSecureCore)
		freePlain  = server("US", api.TierFree, 0)
		offline    = api.LogicalServer{Name: "off", ExitCountry: "NL", Tier: api.TierPlus, Status: 0, Servers: []api.PhysicalServer{{}}}
		noPhysical = api.LogicalServer{Name: "empty", ExitCountry: "NL", Tier: api.TierPlus, Status: constants.StatusOnline}
	)
	all := []api.LogicalServer{plusP2P, plusPlain, plusCore, freePlain, offline, noPhysical}

	tests := []struct {
		name string
		cfg  config.Config
		want []string
	}{
		{
			// Offline and physical-server-less entries never survive, and free
			// tier is excluded unless asked for.
			name: "defaults exclude offline, empty and free",
			want: []string{nlServer, nlServer, chServer},
		},
		{
			name: "country filter",
			cfg:  config.Config{Countries: []string{"CH"}},
			want: []string{chServer},
		},
		{
			name: "empty country list matches everything",
			cfg:  config.Config{Countries: nil},
			want: []string{nlServer, nlServer, chServer},
		},
		{
			name: "free-only swaps the tier filter rather than widening it",
			cfg:  config.Config{FreeOnly: true},
			want: []string{usServer},
		},
		{
			name: "p2p-only",
			cfg:  config.Config{P2PServersOnly: true},
			want: []string{nlServer},
		},
		{
			// P2P is suppressed by secure-core and by free-only, otherwise those
			// combinations would filter each other down to nothing.
			name: "secure-core suppresses the p2p filter",
			cfg:  config.Config{P2PServersOnly: true, SecureCoreOnly: true},
			want: []string{chServer},
		},
		{
			name: "free-only suppresses the p2p filter",
			cfg:  config.Config{P2PServersOnly: true, FreeOnly: true},
			want: []string{usServer},
		},
		{
			name: "secure-core alone",
			cfg:  config.Config{SecureCoreOnly: true},
			want: []string{chServer},
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := EligibleServers(&tt.cfg, all)
			names := make([]string, len(got))
			for i := range got {
				names[i] = got[i].Name
			}
			if len(names) != len(tt.want) {
				t.Fatalf("got %v, want %v", names, tt.want)
			}
			for i := range names {
				if names[i] != tt.want[i] {
					t.Fatalf("got %v, want %v", names, tt.want)
				}
			}
		})
	}
}

func TestGetBestPhysicalServer(t *testing.T) {
	online := api.PhysicalServer{ID: "on", Status: constants.StatusOnline}
	down := api.PhysicalServer{ID: "down", Status: 0}

	tests := []struct {
		name string
		in   []api.PhysicalServer
		want string // "" means nil
	}{
		{name: "picks the first online", in: []api.PhysicalServer{down, online}, want: "on"},
		{name: "no physical servers", in: nil},
		// An offline endpoint would yield a config pointing at a dead server.
		{name: "all offline yields nil", in: []api.PhysicalServer{down, down}},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := GetBestPhysicalServer(&api.LogicalServer{Servers: tt.in})
			if tt.want == "" {
				if got != nil {
					t.Fatalf("got %+v, want nil", got)
				}
				return
			}
			if got == nil || got.ID != tt.want {
				t.Fatalf("got %+v, want ID %q", got, tt.want)
			}
		})
	}
}

func TestGetBestWireGuardPhysicalServerSkipsBrokenPeers(t *testing.T) {
	validKey := base64.StdEncoding.EncodeToString(make([]byte, 32))
	logical := api.LogicalServer{Servers: []api.PhysicalServer{
		{Status: constants.StatusOnline, EntryIP: "not-an-ip", X25519PublicKey: validKey},
		{Status: constants.StatusOnline, EntryIP: "192.0.2.1", X25519PublicKey: "bad-key"},
		{Status: constants.StatusOnline, EntryIP: "192.0.2.2", X25519PublicKey: validKey},
	}}
	got := GetBestWireGuardPhysicalServer(&logical)
	if got == nil || got.EntryIP != "192.0.2.2" {
		t.Fatalf("expected the first usable peer, got %+v", got)
	}
	logical.Servers[2].ServicesDownReason = "maintenance"
	if GetBestWireGuardPhysicalServer(&logical) != nil {
		t.Fatal("peer marked with a service-down reason must not enter the speed test")
	}
}

func TestSelectBestWithPing(t *testing.T) {
	srv1 := api.LogicalServer{
		Name:        "US-EAST#1",
		ExitCountry: "US",
		Tier:        api.TierFree,
		Status:      constants.StatusOnline,
		Load:        20,
		Score:       1.5,
		Servers:     []api.PhysicalServer{{Status: constants.StatusOnline, EntryIP: "127.0.0.1"}},
	}
	srv2 := api.LogicalServer{
		Name:        "NL#1",
		ExitCountry: "NL",
		Tier:        api.TierFree,
		Status:      constants.StatusOnline,
		Load:        50,
		Score:       2.5,
		Servers:     []api.PhysicalServer{{Status: constants.StatusOnline, EntryIP: "127.0.0.1"}},
	}

	cfg := &config.Config{
		FreeOnly: true,
		AutoPing: false,
	}
	selector := NewServerSelector(cfg)
	best, ping, err := selector.SelectBestWithPing([]api.LogicalServer{srv2, srv1})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if best.Name != "US-EAST#1" {
		t.Fatalf("expected US-EAST#1 to be selected by score, got %s", best.Name)
	}
	if ping != 0 {
		t.Fatalf("expected ping=0 when AutoPing=false, got %d", ping)
	}
}

func TestRegionalCandidatesCoverLocationsBeyondGlobalTopTen(t *testing.T) {
	var all []api.LogicalServer
	for i := range 12 {
		srv := server("US", api.TierFree, 0)
		srv.Name = fmt.Sprintf("US#%d", i)
		srv.Load = i
		srv.Servers[0].EntryIP = "192.0.2.1"
		all = append(all, srv)
	}
	for _, city := range []string{"Tokyo", "Osaka"} {
		srv := server("JP", api.TierFree, 0)
		srv.City, srv.Name, srv.Score = city, city, 1000
		srv.Servers[0].EntryIP = "192.0.2.2"
		all = append(all, srv)
	}
	got := regionalCandidates(all)
	if len(got) != 4 {
		t.Fatalf("expected two US candidates and both Japanese cities, got %v", got)
	}
	if got[1].City != "Osaka" || got[2].City != "Tokyo" {
		t.Fatalf("locations must precede alternate candidates: %v", got)
	}
	if all[0].Name != "US#0" {
		t.Fatal("input was mutated")
	}
}

func TestLatencyFirstWithCongestionGuard(t *testing.T) {
	tests := []struct {
		name         string
		loads, pings [2]int
		want         string
	}{
		{"9 ms beats 130 ms even with more load", [2]int{20, 1}, [2]int{9, 130}, "A"},
		{"congestion can still beat a slightly faster full-ish route", [2]int{85, 10}, [2]int{20, 30}, "B"},
		{"a much faster route wins over a far slower empty one", [2]int{80, 20}, [2]int{10, 200}, "A"},
		{"equal pings prefer the emptier server", [2]int{30, 70}, [2]int{50, 50}, "A"},
		{"a 97 percent server never hides a healthy one", [2]int{97, 40}, [2]int{40, 60}, "B"},
		{"failed ping never beats reachable server", [2]int{0, 95}, [2]int{999, 200}, "B"},
		{"unknown ping never beats reachable server", [2]int{0, 95}, [2]int{0, 200}, "B"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			candidates := []api.LogicalServer{{Name: "A", Load: tt.loads[0]}, {Name: "B", Load: tt.loads[1], Score: 10000}}
			got, ping, err := bestMeasuredCandidate(candidates, map[string]int{"A": tt.pings[0], "B": tt.pings[1]})
			wantPing := tt.pings[0]
			if tt.want == "B" {
				wantPing = tt.pings[1]
			}
			if err != nil || got.Name != tt.want || ping != wantPing {
				t.Fatalf("got %v, %d, %v", got, ping, err)
			}
		})
	}
	if got, _, err := bestMeasuredCandidate([]api.LogicalServer{{Name: "A"}}, nil); err == nil || got != nil {
		t.Fatal("all failed probes must fail selection")
	}
}

func TestFullServersOnlyWhenNothingElse(t *testing.T) {
	candidates := []api.LogicalServer{{Name: "full", Load: 97}, {Name: "fuller", Load: 99, Score: 2}}
	got, ping, err := bestMeasuredCandidate(candidates, map[string]int{"full": 20, "fuller": 10})
	if err != nil || got.Name != "fuller" || ping != 10 {
		t.Fatalf("all full servers should still select the least costly: %v, %d, %v", got, ping, err)
	}
}

func TestSpeedFinalistsPrioritizeLowestPingAndAllowBlockedICMP(t *testing.T) {
	candidates := []api.LogicalServer{
		{Name: "US1", ExitCountry: "US", City: "NY", Load: 10},
		{Name: "US2", ExitCountry: "US", City: "NY", Load: 11},
		{Name: "JP", ExitCountry: "JP", City: "Tokyo", Load: 20},
	}
	got, err := speedFinalists(candidates, map[string]int{"US1": 50, "US2": 50, "JP": 999}, 3)
	if err != nil || len(got) != 3 || got[0].Name != "US1" || got[1].Name != "US2" || got[2].Name != "JP" {
		t.Fatalf("got %v %v", got, err)
	}
	if candidates[1].Name != "US2" {
		t.Fatal("mutated input")
	}
}

func TestSpeedFinalistsUseFallbacksWhenEveryPingIsBlocked(t *testing.T) {
	candidates := []api.LogicalServer{
		{Name: "low-load", Load: 10},
		{Name: "high-load", Load: 80},
	}
	got, err := speedFinalists(candidates, map[string]int{"low-load": 999, "high-load": 999}, 2)
	if err != nil || len(got) != 2 || got[0].Name != "low-load" || got[1].Name != "high-load" {
		t.Fatalf("all blocked probes should retain ordered fallbacks: got %v err=%v", got, err)
	}
}

func TestSpeedFinalistsUsePingBeforeLoad(t *testing.T) {
	candidates := []api.LogicalServer{
		{Name: "low-ping", Load: 95},
		{Name: "low-load", Load: 1},
		{Name: "second-low-ping", Load: 80},
	}
	got, err := speedFinalists(candidates, map[string]int{"low-ping": 18, "low-load": 140, "second-low-ping": 32}, 2)
	if err != nil || len(got) != 2 || got[0].Name != "low-ping" || got[1].Name != "second-low-ping" {
		t.Fatalf("ping must choose the six-test shortlist before load: got %v err=%v", got, err)
	}
}

func TestPremiumSpeedFinalistsPreferNearbyBrazilWithinPingWindow(t *testing.T) {
	candidates := []api.LogicalServer{
		{Name: "US-NY#1", ExitCountry: "US", Load: 1},
		{Name: "PE#1", ExitCountry: "PE", Load: 90},
		{Name: "JP#1", ExitCountry: "JP", Load: 1},
	}
	pings := map[string]int{"US-NY#1": 92, "PE#1": 95, "JP#1": 180}
	got, err := speedFinalistsWithPreference(candidates, pings, 2, true)
	if err != nil || len(got) != 2 || got[0].Name != "PE#1" || got[1].Name != "US-NY#1" {
		t.Fatalf("nearby route should win a close RTT comparison: got %v err=%v", got, err)
	}

	// A clearly lower RTT remains the primary signal, even when the route is
	// outside South America.
	pings["US-NY#1"] = 70
	got, err = speedFinalistsWithPreference(candidates, pings, 2, true)
	if err != nil || len(got) != 2 || got[0].Name != "US-NY#1" {
		t.Fatalf("nearby preference must not override a clearly lower RTT: got %v err=%v", got, err)
	}
}

func TestExcludedCountriesCannotWinAutomaticSelection(t *testing.T) {
	servers := []api.LogicalServer{server("BR", api.TierFree, 0), server("US", api.TierFree, 0)}
	cfg := &config.Config{FreeOnly: true, ExcludedCountries: []string{"BR"}}
	got := EligibleServers(cfg, servers)
	if len(got) != 1 || got[0].ExitCountry != "US" {
		t.Fatalf("blocked exit eligible: %v", got)
	}
	cfg.Countries = []string{"BR"}
	if len(EligibleServers(cfg, servers)) != 0 {
		t.Fatal("country preference must not override exclusion")
	}
}

// smartServer builds an online Plus server with a valid WireGuard peer, so the
// speed pipeline can normalize it without touching the network.
func smartServer(name, exit, host string, load, score int) api.LogicalServer {
	return api.LogicalServer{
		Name:        name,
		ExitCountry: exit,
		HostCountry: host,
		Tier:        api.TierPlus,
		Status:      constants.StatusOnline,
		Load:        load,
		Score:       float64(score),
		Servers: []api.PhysicalServer{{
			Status:          constants.StatusOnline,
			EntryIP:         "127.0.0.1",
			X25519PublicKey: base64.StdEncoding.EncodeToString(make([]byte, 32)),
		}},
	}
}

// G1: the exclusion is about where the machine is, not only the exit label.
func TestExcludedHostCountryIsNotEligible(t *testing.T) {
	hosted := smartServer("SV#43", "SV", "BR", 5, 1)
	cfg := &config.Config{ExcludedCountries: []string{"BR"}}
	if isEligible(cfg, &hosted) {
		t.Fatal("a BR-hosted server labelled SV must not be eligible when BR is excluded")
	}
	if got := EligibleServers(cfg, []api.LogicalServer{hosted}); len(got) != 0 {
		t.Fatalf("eligible list kept a BR-hosted server: %v", got)
	}
	// The same server stays eligible when Brazil is not excluded.
	cfg.ExcludedCountries = nil
	if !isEligible(cfg, &hosted) {
		t.Fatal("host exclusion must only apply to configured countries")
	}
}

// G2: automatic selection skips a cheaper Smart Routing host, and still returns
// one when every candidate is Smart Routing.
func TestAutomaticSelectionSkipsSmartRoutingHosts(t *testing.T) {
	smart := smartServer("SV#43", "SV", "US", 5, 1)
	plain := smartServer("AR#7", "AR", "", 40, 2)

	cfg := &config.Config{AutoPing: false}
	best, _, err := NewServerSelector(cfg).SelectBestWithPing([]api.LogicalServer{smart, plain})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if best.Name != "AR#7" {
		t.Fatalf("automatic selection chose the Smart Routing host: %s", best.Name)
	}

	cfg.Countries = []string{"SV"}
	best, _, err = NewServerSelector(cfg).SelectBestWithPing([]api.LogicalServer{smart})
	if err != nil || best == nil || best.Name != "SV#43" {
		t.Fatalf("an explicit country must keep its Smart Routing server: %v %v", best, err)
	}

	// Speed candidates: the same filter applies before the regional shortlist.
	cfg = &config.Config{AutoPing: true}
	finalists, _, err := NewServerSelector(cfg).SpeedCandidatesWithProgress([]api.LogicalServer{smart, plain}, 1, nil)
	if err != nil || len(finalists) != 1 || finalists[0].Name != "AR#7" {
		t.Fatalf("speed candidates kept the Smart Routing host: %v %v", finalists, err)
	}

	// Fallback: Smart Routing being the only option must still select it.
	best, _, err = NewServerSelector(&config.Config{AutoPing: false}).SelectBestWithPing([]api.LogicalServer{smart})
	if err != nil || best == nil || best.Name != "SV#43" {
		t.Fatalf("fallback must return the only Smart Routing server: %v %v", best, err)
	}
}

// G3: an explicit country request keeps Smart Routing servers, even when the
// host country itself is excluded. A non-Smart-Routing alternative proves the
// exclusion was not applied to this mode.
func TestExplicitCountryKeepsSmartRoutingServer(t *testing.T) {
	smart := smartServer("SV#43", "SV", "US", 5, 1)
	plain := smartServer("SV#9", "SV", "", 40, 2)
	cfg := &config.Config{Countries: []string{"SV"}, ExcludedCountries: []string{"BR"}, AutoPing: false}
	if got := EligibleServers(cfg, []api.LogicalServer{smart, plain}); len(got) != 2 {
		t.Fatalf("explicit country must keep both SV servers: %v", got)
	}
	best, _, err := NewServerSelector(cfg).SelectBestWithPing([]api.LogicalServer{smart, plain})
	if err != nil || best == nil || best.Name != "SV#43" {
		t.Fatalf("explicit country selection lost its Smart Routing server: %v %v", best, err)
	}
}
