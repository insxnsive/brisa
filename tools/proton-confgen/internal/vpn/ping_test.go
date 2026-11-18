package vpn

import (
	"context"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"protonvpn-wg-confgen/internal/api"
)

func TestProbeCandidatesDeduplicatesPhysicalIPs(t *testing.T) {
	a := server("US", api.TierFree, 0)
	a.Servers[0].EntryIP = "192.0.2.1"
	b := a
	b.Name = "US#2"
	var calls atomic.Int32
	result := probeCandidates(context.Background(), []api.LogicalServer{a, b}, func(context.Context, string) int {
		calls.Add(1)
		return 42
	})
	if calls.Load() != 1 || result[a.Name] != 42 || result[b.Name] != 42 {
		t.Fatalf("calls=%d results=%v", calls.Load(), result)
	}
}

func TestProbeCandidatesProgressCountsEachLogicalRoute(t *testing.T) {
	a := server("US", api.TierFree, 0)
	a.Name = "US#1"
	a.Servers[0].EntryIP = "192.0.2.1"
	b := a
	b.Name = "US#2"
	c := a
	c.Name = "NL#1"
	c.Servers = append([]api.PhysicalServer(nil), a.Servers...)
	c.Servers[0].EntryIP = "192.0.2.2"
	var events []PingProgressEvent
	result := probeCandidatesWithProgress(context.Background(), []api.LogicalServer{a, b, c}, func(_ context.Context, ip string) int {
		if ip == "192.0.2.2" {
			return 999
		}
		return 24
	}, func(event PingProgressEvent) {
		events = append(events, event)
	})
	if len(events) != 4 || len(result) != 3 {
		t.Fatalf("events=%d results=%v; expected one event/result per logical route", len(events), result)
	}
	if events[len(events)-1].Tested != 3 || events[len(events)-1].Succeeded != 2 {
		t.Fatalf("unexpected final progress: %+v", events[len(events)-1])
	}
	seen := map[string]string{}
	for _, event := range events[1:] {
		if event.Total != 3 || event.Tested < 1 || event.Tested > 3 {
			t.Fatalf("invalid progress counters: %+v", event)
		}
		seen[event.Server] = event.Status
	}
	if seen["US#1"] != "success" || seen["US#2"] != "success" || seen["NL#1"] != "failed" {
		t.Fatalf("unexpected route statuses: %+v", seen)
	}
}

func TestProbeCandidatesRespectsCancellation(t *testing.T) {
	a := server("US", api.TierFree, 0)
	a.Servers[0].EntryIP = "192.0.2.1"
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	result := probeCandidates(ctx, []api.LogicalServer{a}, func(context.Context, string) int {
		t.Error("canceled scan must not probe")
		return 42
	})
	if len(result) != 0 {
		t.Fatalf("unexpected measurements: %v", result)
	}
}

func TestProbeCandidatesCancelsInFlightProbe(t *testing.T) {
	a := server("US", api.TierFree, 0)
	a.Servers[0].EntryIP = "192.0.2.1"
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan map[string]int, 1)
	started := make(chan struct{})
	go func() {
		done <- probeCandidates(ctx, []api.LogicalServer{a}, func(ctx context.Context, _ string) int {
			close(started)
			<-ctx.Done()
			return 999
		})
	}()
	<-started
	cancel()
	select {
	case result := <-done:
		if result[a.Name] != 999 {
			t.Fatalf("canceled measurement is not failure: %v", result)
		}
	case <-time.After(time.Second):
		t.Fatal("scan did not stop")
	}
}

func TestPhaseBOnlyProbesFailedLocations(t *testing.T) {
	mk := func(name, country, city, ip string) api.LogicalServer {
		srv := server(country, api.TierFree, 0)
		srv.Name, srv.City = name, city
		srv.Servers[0].EntryIP = ip
		return srv
	}
	jp1 := mk("JP#1", "JP", "Tokyo", "192.0.2.10")
	jp2 := mk("JP#2", "JP", "Tokyo", "192.0.2.11")
	nl1 := mk("NL#1", "NL", "Amsterdam", "192.0.2.20")
	nl2 := mk("NL#2", "NL", "Amsterdam", "192.0.2.21")
	var mu sync.Mutex
	var calls []string
	probeCandidates(context.Background(), []api.LogicalServer{jp1, jp2, nl1, nl2}, func(_ context.Context, ip string) int {
		mu.Lock()
		calls = append(calls, ip)
		mu.Unlock()
		switch ip {
		case "192.0.2.10":
			return 24
		case "192.0.2.11":
			t.Error("phase B probed a location whose first candidate answered")
		}
		return 999
	})
	seen := map[string]bool{}
	for _, ip := range calls {
		seen[ip] = true
	}
	if !seen["192.0.2.10"] || !seen["192.0.2.20"] || !seen["192.0.2.21"] {
		t.Fatalf("expected both first candidates and the failed location second: %v", calls)
	}
}

func TestCandidateScanHonoursDeadline(t *testing.T) {
	a := server("US", api.TierFree, 0)
	a.Servers[0].EntryIP = "192.0.2.1"
	ctx, cancel := context.WithTimeout(context.Background(), 80*time.Millisecond)
	defer cancel()
	started := time.Now()
	result := probeCandidates(ctx, []api.LogicalServer{a}, func(ctx context.Context, _ string) int {
		<-ctx.Done()
		return 999
	})
	if elapsed := time.Since(started); elapsed > time.Second {
		t.Fatalf("deadline was not honoured: %v", elapsed)
	}
	if result[a.Name] != 999 {
		t.Fatalf("timed out probe is not a failure: %v", result)
	}
}
