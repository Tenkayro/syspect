package diagnostics

import (
	"context"
	"os"
	"os/exec"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
)

type Result struct {
	Category    string `json:"category"`
	Name        string `json:"name"`
	Status      string `json:"status"`
	Value       any    `json:"value,omitempty"`
	Explanation string `json:"explanation,omitempty"`
	Details     string `json:"details,omitempty"`
	Severity    string `json:"severity,omitempty"`
}
type Run struct {
	Started     string         `json:"scan_started_at"`
	Duration    float64        `json:"duration_seconds"`
	Profile     string         `json:"profile"`
	Results     []Result       `json:"results"`
	Completed   []string       `json:"completed_categories"`
	Interrupted bool           `json:"interrupted"`
	Summary     map[string]any `json:"summary"`
	CommandLog  []string       `json:"command_log,omitempty"`
}
type Category struct{ ID, Name string }

var Categories = []Category{
	{"os", "Operating System"},
	{"cpu", "CPU"},
	{"memory", "Memory"},
	{"gpu", "GPU & Graphics"},
	{"board", "Motherboard & Firmware"},
	{"storage", "Storage"},
	{"network", "Network"},
	{"sensors", "Temperatures & Sensors"},
	{"battery", "Battery & Power"},
	{"kernel", "Kernel & Drivers"},
	{"services", "Services & Processes"},
	{"logs", "System Logs"},
	{"virtualization", "Virtualization & Containers"},
	{"security", "Security & Hardening"},
	{"dns", "DNS Benchmark"},
}

// ProfileCategories returns the categories used by a named scan profile.
func ProfileCategories(profile string) []Category {
	ids := map[string][]string{
		"quick":    {"os", "cpu", "memory", "gpu", "storage", "security", "virtualization"},
		"hardware": {"cpu", "memory", "gpu", "board", "storage", "network", "sensors", "battery"},
		"server":   {"os", "cpu", "memory", "storage", "network", "kernel", "services", "logs", "security", "virtualization"},
		"dns":      {"dns"},
		"full":     {"os", "cpu", "memory", "gpu", "board", "storage", "network", "sensors", "battery", "kernel", "services", "logs", "security", "virtualization"},
	}[profile]
	if ids == nil {
		return nil
	}
	out := []Category{}
	for _, want := range ids {
		for _, cat := range Categories {
			if cat.ID == want {
				out = append(out, cat)
			}
		}
	}
	return out
}

func command(ctx context.Context, timeout time.Duration, name string, args ...string) (string, string, int) {
	c, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	cmd := exec.CommandContext(c, name, args...)
	cmd.Env = append(os.Environ(), "LC_ALL=C")
	out, e := cmd.Output()
	if e == nil {
		return string(out), "", 0
	}
	err := ""
	if ee, ok := e.(*exec.ExitError); ok {
		err = string(ee.Stderr)
	} else {
		err = e.Error()
	}
	code := 1
	if ee, ok := e.(*exec.ExitError); ok {
		code = ee.ExitCode()
	}
	if c.Err() == context.DeadlineExceeded {
		err = "Command timed out"
		code = 124
	}
	return string(out), strings.TrimSpace(err), code
}
func read(p string) string { b, _ := os.ReadFile(p); return strings.TrimSpace(string(b)) }
func pass(c, n string, v any, detail string) Result {
	return Result{c, n, "PASS", v, "Collected successfully.", detail, "info"}
}
func skip(c, n, why string) Result { return Result{c, n, "SKIPPED", nil, why, "", "info"} }
func warn(c, n string, v any, why string) Result {
	return Result{c, n, "WARNING", v, why, "", "warning"}
}

func Collect(ctx context.Context, cfg config.Config, cat Category) []Result {
	c := cat.Name
	timeout := time.Duration(cfg.Timeout) * time.Second
	if timeout < 2*time.Second {
		timeout = 12 * time.Second
	}
	switch cat.ID {
	case "os":
		return collectOS(c, cfg)
	case "cpu":
		return collectCPU(c, cfg)
	case "memory":
		return collectMemory(c, cfg)
	case "gpu":
		return collectGPU(ctx, c, timeout, cfg)
	case "board":
		return collectBoard(ctx, c, timeout, cfg)
	case "storage":
		return collectStorage(ctx, c, timeout, cfg)
	case "network":
		return collectNetwork(ctx, c, timeout, cfg)
	case "sensors":
		return collectSensors(ctx, c, timeout)
	case "battery":
		return collectBattery(c, cfg)
	case "kernel":
		return collectKernel(c, cfg)
	case "services":
		return collectServices(ctx, c, timeout, cfg)
	case "logs":
		return collectLogs(ctx, c, timeout, cfg)
	case "virtualization":
		return collectVirtualization(ctx, c, timeout, cfg)
	case "security":
		return collectSecurity(ctx, c, timeout)
	case "dns":
		return collectDNS(ctx, c, timeout, cfg)
	}
	return []Result{skip(c, "Diagnostic category", "Not implemented.")}
}

func containsAny(s string, words ...string) []string {
	out := []string{}
	l := strings.ToLower(s)
	for _, w := range words {
		if strings.Contains(l, w) {
			out = append(out, w)
		}
	}
	if len(out) == 0 {
		return []string{"Not detected"}
	}
	return out
}
func (r *Run) Finalize() {
	counts := map[string]int{"pass": 0, "warning": 0, "fail": 0, "skipped": 0}
	for _, x := range r.Results {
		switch x.Status {
		case "PASS":
			counts["pass"]++
		case "WARNING":
			counts["warning"]++
		case "FAIL":
			counts["fail"]++
		default:
			counts["skipped"]++
		}
	}
	overall := "PASS"
	if counts["warning"] > 0 {
		overall = "WARNING"
	}
	if counts["fail"] > 0 {
		overall = "FAIL"
	}
	if r.Interrupted {
		overall = "INCOMPLETE"
	}
	r.Summary = map[string]any{"overall": overall, "total": len(r.Results), "pass": counts["pass"], "warning": counts["warning"], "fail": counts["fail"], "skipped": counts["skipped"]}
}
