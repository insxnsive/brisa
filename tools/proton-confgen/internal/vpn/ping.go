package vpn

import (
	"context"
	"math"
	"net"
	"os/exec"
	"regexp"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"

	"protonvpn-wg-confgen/internal/api"
)

var (
	pingLinuxRegex   = regexp.MustCompile(`(?:time|tempo)=([0-9.]+)\s*ms`)
	pingWindowsRegex = regexp.MustCompile(`(?:time|tempo)[=<]([0-9]+)ms`)
)

// ProbePing measures round-trip time (RTT) to an IP address in milliseconds.
func ProbePing(ip string, timeout time.Duration) int {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	return probePingContext(ctx, ip)
}

func probePingContext(ctx context.Context, ip string) int {
	if ip == "" {
		return 999
	}

	// 1. In-process ICMP on Windows, then the system ping command as a fallback.
	if ms, err := fastIcmpPing(ctx, ip); err == nil && ms > 0 {
		return ms
	}
	if ms, err := systemPingContext(ctx, ip); err == nil && ms > 0 {
		return ms
	}

	// 2. Fallback: TCP probe to port 443 or 80
	for _, port := range []string{"443", "80"} {
		start := time.Now()
		conn, err := (&net.Dialer{}).DialContext(ctx, "tcp", net.JoinHostPort(ip, port))
		if err == nil {
			_ = conn.Close()
			return max(1, int(time.Since(start).Milliseconds()))
		}
	}

	return 999
}

const (
	candidateProbeTimeout = 700 * time.Millisecond
	candidateWorkers      = 64
	pingDeadline          = 6 * time.Second
)

func systemPing(ip string, timeout time.Duration) (int, error) {
	ctx, cancel := context.WithTimeout(context.Background(), timeout)
	defer cancel()
	return systemPingContext(ctx, ip)
}

func systemPingContext(ctx context.Context, ip string) (int, error) {
	// Reserve time for TCP fallback when ICMP is filtered.
	icmpCtx, cancel := context.WithTimeout(ctx, 600*time.Millisecond)
	defer cancel()
	var cmd *exec.Cmd
	if runtime.GOOS == "windows" {
		cmd = exec.CommandContext(icmpCtx, "ping", "-n", "1", "-w", "600", ip)
	} else {
		// Linux / macOS: 1 packet, 1s deadline
		cmd = exec.CommandContext(icmpCtx, "ping", "-c", "1", "-W", "1", ip)
	}

	out, err := cmd.CombinedOutput()
	if err != nil {
		return 0, err
	}

	text := strings.ToLower(string(out))
	if runtime.GOOS == "windows" {
		if strings.Contains(text, "<1ms") {
			return 1, nil
		}
		matches := pingWindowsRegex.FindStringSubmatch(text)
		if len(matches) > 1 {
			if val, err := strconv.Atoi(matches[1]); err == nil {
				return val, nil
			}
		}
	} else {
		matches := pingLinuxRegex.FindStringSubmatch(text)
		if len(matches) > 1 {
			if val, err := strconv.ParseFloat(matches[1], 64); err == nil {
				return max(1, int(math.Round(val))), nil
			}
		}
	}

	return 0, nil
}

// PingProgressEvent describes one logical route after its physical endpoint has
// been measured. A shared physical IP is probed once, but every logical route
// that points to it receives its own event so the progress count remains honest.
type PingProgressEvent struct {
	Total     int
	Tested    int
	Succeeded int
	Server    string
	PingMs    int
	ElapsedMs int
	Status    string
}

// PingProgressFunc receives ping progress synchronously after each route is
// accounted for. It is intentionally independent from speedtest.ProgressFunc
// so the VPN package does not depend on the measurement package.
type PingProgressFunc func(PingProgressEvent)

// ProbeCandidatesPing covers the regional candidates with bounded concurrency
// and a shared deadline. Shared entry IPs are measured once, avoiding duplicate
// probes to the same physical machine. Missing/failed probes are not winners.
func ProbeCandidatesPing(servers []api.LogicalServer, maxCandidates int) map[string]int {
	return ProbeCandidatesPingWithProgress(servers, maxCandidates, nil)
}

// ProbeCandidatesPingWithProgress is the observable form of
// ProbeCandidatesPing. The callback is used by the GUI and the terminal trace
// so both callers see the same ping stage and the same counters.
func ProbeCandidatesPingWithProgress(servers []api.LogicalServer, maxCandidates int, progress PingProgressFunc) map[string]int {
	ctx, cancel := context.WithTimeout(context.Background(), pingDeadline)
	defer cancel()
	return probeCandidatesWithProgress(ctx, servers[:max(0, min(maxCandidates, len(servers)))], probePingContext, progress)
}

func probeCandidates(ctx context.Context, servers []api.LogicalServer, probe func(context.Context, string) int) map[string]int {
	return probeCandidatesWithProgress(ctx, servers, probe, nil)
}

type pingOutcome struct {
	ip        string
	ms        int
	elapsedMs int
}

func locationKey(srv api.LogicalServer) string {
	return strings.ToUpper(srv.ExitCountry) + "|" + strings.ToLower(strings.TrimSpace(srv.Region)) + "|" + strings.ToLower(strings.TrimSpace(srv.City))
}

func validPing(ms int) bool { return ms > 0 && ms < 999 }

// probeIPs measures a set of addresses with bounded concurrency. A canceled context stops new
// probes; one already in flight returns its failure and is still reported.
func probeIPs(ctx context.Context, ips []string, probe func(context.Context, string) int) map[string]pingOutcome {
	outcomes := make(map[string]pingOutcome, len(ips))
	if len(ips) == 0 {
		return outcomes
	}
	jobs := make(chan string)
	results := make(chan pingOutcome, len(ips))
	var wg sync.WaitGroup
	for range min(candidateWorkers, len(ips)) {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for ip := range jobs {
				if ctx.Err() != nil {
					continue
				}
				probeCtx, cancel := context.WithTimeout(ctx, candidateProbeTimeout)
				started := time.Now()
				ms := probe(probeCtx, ip)
				cancel()
				results <- pingOutcome{ip: ip, ms: ms, elapsedMs: max(0, int(time.Since(started).Milliseconds()))}
			}
		}()
	}
	go func() {
		defer close(results)
		defer wg.Wait()
		defer close(jobs)
	send:
		for _, ip := range ips {
			select {
			case jobs <- ip:
			case <-ctx.Done():
				break send
			}
		}
	}()
	for outcome := range results {
		outcomes[outcome.ip] = outcome
	}
	return outcomes
}

func probeCandidatesWithProgress(ctx context.Context, servers []api.LogicalServer, probe func(context.Context, string) int, progress PingProgressFunc) map[string]int {
	results := make(map[string]int)
	if ctx.Err() != nil {
		return results
	}
	byIP := make(map[string][]string)
	groups := make(map[string][2]string)
	var order, ips, missing []string
	for _, srv := range servers {
		phys := GetBestPhysicalServer(&srv)
		if phys == nil || phys.EntryIP == "" {
			missing = append(missing, srv.Name)
			continue
		}
		ip := phys.EntryIP
		if _, exists := byIP[ip]; !exists {
			ips = append(ips, ip)
		}
		byIP[ip] = append(byIP[ip], srv.Name)
		key := locationKey(srv)
		group, exists := groups[key]
		if !exists {
			order = append(order, key)
		}
		if group[0] == "" {
			group[0] = ip
		} else if group[1] == "" && ip != group[0] {
			group[1] = ip
		}
		groups[key] = group
	}

	tested, succeeded := 0, 0
	if progress != nil {
		progress(PingProgressEvent{Total: len(servers)})
	}
	emit := func(server string, ms, elapsedMs int) {
		tested++
		status := "failed"
		if validPing(ms) {
			succeeded++
			status = "success"
		}
		if progress != nil {
			progress(PingProgressEvent{
				Total:     len(servers),
				Tested:    tested,
				Succeeded: succeeded,
				Server:    server,
				PingMs:    ms,
				ElapsedMs: elapsedMs,
				Status:    status,
			})
		}
	}

	// Phase A probes the first candidate of every location. Phase B touches the second one only
	// when the first failed, which roughly halves the normal workload.
	measured := make(map[string]pingOutcome, len(ips))
	seen := make(map[string]bool, len(ips))
	phaseA := make([]string, 0, len(order))
	for _, key := range order {
		ip := groups[key][0]
		if ip != "" && !seen[ip] {
			seen[ip] = true
			phaseA = append(phaseA, ip)
		}
	}
	for ip, outcome := range probeIPs(ctx, phaseA, probe) {
		measured[ip] = outcome
	}
	phaseB := make([]string, 0, len(order))
	for _, key := range order {
		second := groups[key][1]
		if second == "" || seen[second] {
			continue
		}
		if outcome, ok := measured[groups[key][0]]; ok && validPing(outcome.ms) {
			continue
		}
		seen[second] = true
		phaseB = append(phaseB, second)
	}
	for ip, outcome := range probeIPs(ctx, phaseB, probe) {
		measured[ip] = outcome
	}

	for _, name := range missing {
		emit(name, 999, 0)
	}
	for _, ip := range ips {
		outcome, ok := measured[ip]
		ms := 999
		elapsed := 0
		if ok {
			ms, elapsed = outcome.ms, outcome.elapsedMs
		}
		for _, name := range byIP[ip] {
			results[name] = ms
			emit(name, ms, elapsed)
		}
	}
	return results
}
