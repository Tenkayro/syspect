package diagnostics

import (
	"context"
	"errors"
	"fmt"
	"math"
	"net"
	"os"
	"sort"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
)

type dnsResolver struct {
	Name   string
	Server string
}

type dnsPhaseStats struct {
	Samples  int     `json:"samples"`
	Failures int     `json:"failures"`
	AvgMs    float64 `json:"avg_ms"`
	MedianMs float64 `json:"median_ms"`
	P90Ms    float64 `json:"p90_ms"`
	P95Ms    float64 `json:"p95_ms"`
	P99Ms    float64 `json:"p99_ms"`
	MinMs    float64 `json:"min_ms"`
	MaxMs    float64 `json:"max_ms"`
	StdDevMs float64 `json:"stddev_ms"`
}

type dnsPhase struct {
	durations   []float64
	failures    int
	consecutive int
	aborted     bool
}

func (p *dnsPhase) query(ctx context.Context, server, domain string) {
	if p.aborted {
		return
	}
	if ctx.Err() != nil {
		p.aborted = true
		return
	}
	ms, ok := measureDNS(ctx, server, domain)
	if ok {
		p.durations = append(p.durations, ms)
		p.consecutive = 0
		return
	}
	p.failures++
	p.consecutive++
	if p.consecutive >= 4 {
		p.aborted = true
	}
}

func measureDNS(ctx context.Context, server, domain string) (float64, bool) {
	resolver := &net.Resolver{
		PreferGo: true,
		Dial: func(ctx context.Context, network, address string) (net.Conn, error) {
			proto := "udp"
			if strings.HasPrefix(network, "tcp") {
				proto = "tcp"
			}
			d := net.Dialer{Timeout: 1500 * time.Millisecond}
			return d.DialContext(ctx, proto, net.JoinHostPort(server, "53"))
		},
	}
	qctx, cancel := context.WithTimeout(ctx, 1500*time.Millisecond)
	defer cancel()
	start := time.Now()
	_, err := resolver.LookupIP(qctx, "ip4", domain)
	ms := float64(time.Since(start).Microseconds()) / 1000
	if err == nil {
		return ms, true
	}
	var dnsErr *net.DNSError
	if errors.As(err, &dnsErr) && dnsErr.IsNotFound {
		return ms, true // NXDOMAIN still carries a valid latency measurement.
	}
	return ms, false
}

func dnsResolvers() []dnsResolver {
	public := []dnsResolver{
		{"Cloudflare", "1.1.1.1"},
		{"Google", "8.8.8.8"},
		{"Quad9", "9.9.9.9"},
		{"AdGuard DNS", "94.140.14.14"},
		{"Control D", "76.76.2.0"},
		{"Mullvad", "194.242.2.2"},
	}
	system := []dnsResolver{}
	seen := map[string]bool{}
	for _, line := range strings.Split(read("/etc/resolv.conf"), "\n") {
		f := strings.Fields(line)
		if len(f) < 2 || f[0] != "nameserver" {
			continue
		}
		ip := f[1]
		if seen[ip] {
			continue
		}
		seen[ip] = true
		system = append(system, dnsResolver{"System DNS (" + ip + ")", ip})
	}
	out := []dnsResolver{}
	for _, r := range append(system, public...) {
		if seen[r.Server] && !strings.HasPrefix(r.Name, "System DNS") {
			continue
		}
		seen[r.Server] = true
		out = append(out, r)
		if len(out) >= 8 {
			break
		}
	}
	return out
}

func collectDNS(ctx context.Context, c string, timeout time.Duration, cfg config.Config) []Result {
	if !cfg.Network {
		return []Result{skip(c, "DNS latency benchmark", "Network tests are opt-in. Enable 'Network connectivity test' in Settings.")}
	}
	resolvers := dnsResolvers()
	if len(resolvers) == 0 {
		return []Result{skip(c, "DNS latency benchmark", "No DNS resolvers were found.")}
	}

	domains := []string{"google.com", "github.com", "cloudflare.com", "microsoft.com", "wikipedia.org", "amazon.de"}
	warmDomains := []string{"google.com", "github.com", "cloudflare.com"}
	const rounds = 2
	const missQueries = 6

	benchCtx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()

	active := []dnsResolver{}
	unavailable := []dnsResolver{}
	for _, resolver := range resolvers {
		if benchCtx.Err() != nil {
			break
		}
		if _, ok := measureDNS(benchCtx, resolver.Server, "example.com"); ok {
			active = append(active, resolver)
		} else {
			unavailable = append(unavailable, resolver)
		}
	}
	if len(active) == 0 {
		return []Result{warn(c, "DNS latency benchmark", "No resolver responded", "Every probe timed out; check whether UDP port 53 is blocked by the local network or firewall.")}
	}

	type outcome struct {
		resolver dnsResolver
		real     dnsPhaseStats
		warm     dnsPhaseStats
		miss     dnsPhaseStats
	}
	outcomes := []outcome{}
	for _, resolver := range active {
		real := dnsPhase{}
		for round := 0; round < rounds; round++ {
			for _, domain := range domains {
				real.query(benchCtx, resolver.Server, domain)
			}
		}
		warm := dnsPhase{}
		for round := 0; round < 3; round++ {
			for _, domain := range warmDomains {
				warm.query(benchCtx, resolver.Server, domain)
			}
		}
		miss := dnsPhase{}
		stamp := time.Now().UnixNano()
		for i := 0; i < missQueries; i++ {
			name := fmt.Sprintf("syspect-%d-%d-%d.example.com", os.Getpid(), stamp, i)
			miss.query(benchCtx, resolver.Server, name)
		}
		outcomes = append(outcomes, outcome{
			resolver: resolver,
			real:     computeDNSStats(real.durations, real.failures),
			warm:     computeDNSStats(warm.durations, warm.failures),
			miss:     computeDNSStats(miss.durations, miss.failures),
		})
		if benchCtx.Err() != nil {
			break
		}
	}

	results := []Result{}
	bestName, bestMedian := "", 0.0
	for _, o := range outcomes {
		totalSamples := o.real.Samples + o.warm.Samples + o.miss.Samples
		value := map[string]any{
			"resolver":      o.resolver.Server,
			"real_world":    o.real,
			"warm_cache":    o.warm,
			"cache_miss":    o.miss,
			"total_queries": totalSamples + o.real.Failures + o.warm.Failures + o.miss.Failures,
		}
		if totalSamples == 0 {
			results = append(results, warn(c, "Resolver: "+o.resolver.Name, value, "No responses were received from this resolver."))
			continue
		}
		results = append(results, pass(c, "Resolver: "+o.resolver.Name, value, "Latency in milliseconds per phase; lower is better. Cache-miss queries use unique example.com names."))
		if o.real.Samples > 0 {
			if bestName == "" || o.real.MedianMs < bestMedian {
				bestName, bestMedian = o.resolver.Name, o.real.MedianMs
			}
		}
	}
	if bestName != "" {
		results = append(results, pass(c, "Fastest resolver (real-world)", map[string]any{
			"resolver":  bestName,
			"median_ms": bestMedian,
		}, "Ranked by median latency in the real-world phase."))
	}
	for _, resolver := range unavailable {
		results = append(results, skip(c, "Resolver: "+resolver.Name, "No response to the availability probe within 1.5 seconds. The resolver may be blocked by the local network or firewall."))
	}
	if benchCtx.Err() == context.DeadlineExceeded {
		results = append(results, warn(c, "DNS benchmark coverage", "Partial", "The 30 second budget was exhausted; remaining queries were skipped."))
	}
	return results
}

func computeDNSStats(values []float64, failures int) dnsPhaseStats {
	stats := dnsPhaseStats{Failures: failures, Samples: len(values)}
	if len(values) == 0 {
		return stats
	}
	sorted := append([]float64(nil), values...)
	sort.Float64s(sorted)
	sum := 0.0
	for _, v := range sorted {
		sum += v
	}
	avg := sum / float64(len(sorted))
	variance := 0.0
	for _, v := range sorted {
		d := v - avg
		variance += d * d
	}
	variance /= float64(len(sorted))
	stats.AvgMs = round2(avg)
	stats.MedianMs = round2(percentile(sorted, 0.50))
	stats.P90Ms = round2(percentile(sorted, 0.90))
	stats.P95Ms = round2(percentile(sorted, 0.95))
	stats.P99Ms = round2(percentile(sorted, 0.99))
	stats.MinMs = round2(sorted[0])
	stats.MaxMs = round2(sorted[len(sorted)-1])
	stats.StdDevMs = round2(math.Sqrt(variance))
	return stats
}

func percentile(sorted []float64, p float64) float64 {
	if len(sorted) == 0 {
		return 0
	}
	idx := int(math.Ceil(p*float64(len(sorted)))) - 1
	if idx < 0 {
		idx = 0
	}
	if idx >= len(sorted) {
		idx = len(sorted) - 1
	}
	return sorted[idx]
}

func round2(v float64) float64 {
	return math.Round(v*100) / 100
}
