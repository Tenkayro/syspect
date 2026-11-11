package diagnostics

import (
	"context"
	"fmt"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/system"
)

func collectServices(ctx context.Context, c string, timeout time.Duration, cfg config.Config) []Result {
	results := []Result{}
	systemd := system.Init() == "systemd" && system.Which("systemctl")

	if systemd {
		if out, e, code := command(ctx, timeout, "systemctl", "--failed", "--no-legend", "--plain"); code == 0 {
			lines := nonEmptyLines(out)
			if len(lines) == 0 {
				results = append(results, pass(c, "Failed systemd services", "None reported.", ""))
			} else {
				results = append(results, warn(c, "Failed systemd services", lines, "Failed units were reported."))
			}
		} else {
			results = append(results, skip(c, "Failed systemd services", e))
		}

		if out, _, code := command(ctx, timeout, "systemctl", "list-units", "--type=service", "--all", "--no-legend", "--plain", "--no-pager"); code == 0 {
			total, running, failed := 0, 0, 0
			for _, line := range nonEmptyLines(out) {
				f := strings.Fields(line)
				if len(f) < 4 {
					continue
				}
				total++
				if f[2] == "active" && f[3] == "running" {
					running++
				}
				if f[3] == "failed" {
					failed++
				}
			}
			results = append(results, pass(c, "Service counts", map[string]int{
				"total":   total,
				"running": running,
				"failed":  failed,
			}, "Parsed from systemctl list-units."))
		}

		if out, _, code := command(ctx, timeout, "systemctl", "--version"); code == 0 {
			results = append(results, pass(c, "Systemd version", firstLine(out), ""))
		}
	} else {
		results = append(results, skip(c, "Failed systemd services", "systemd is not the detected init system."))
	}

	if system.Which("ps") {
		if o, _, k := command(ctx, timeout, "ps", "-eo", "pid,comm,pcpu,pmem,rss", "--sort=-pcpu"); k == 0 {
			lines := nonEmptyLines(o)
			if len(lines) > 8 {
				lines = lines[:8]
			}
			results = append(results, pass(c, "Top CPU processes", lines, "Snapshot; full command lines are not collected."))
		}
	} else if top := topMemoryProcesses(6); len(top) > 0 {
		results = append(results, pass(c, "Top memory processes", top, "Fallback read directly from /proc."))
	}
	if len(results) == 0 {
		results = append(results, skip(c, "Process inventory", "Neither systemctl nor ps is available."))
	}
	return results
}

func nonEmptyLines(s string) []string {
	out := []string{}
	for _, line := range strings.Split(s, "\n") {
		if strings.TrimSpace(line) != "" {
			out = append(out, line)
		}
	}
	return out
}

func topMemoryProcesses(limit int) []string {
	type process struct {
		name string
		pid  int
		rss  uint64
	}
	procs := []process{}
	for _, dir := range glob("/proc/[0-9]*") {
		pid, err := strconv.Atoi(filepath.Base(dir))
		if err != nil {
			continue
		}
		name := read(dir + "/comm")
		var rss uint64
		for _, line := range strings.Split(read(dir+"/status"), "\n") {
			if !strings.HasPrefix(line, "VmRSS:") {
				continue
			}
			f := strings.Fields(line)
			if len(f) >= 2 {
				if kb, err := strconv.ParseUint(f[1], 10, 64); err == nil {
					rss = kb * 1024
				}
			}
		}
		if name != "" && rss > 0 {
			procs = append(procs, process{name, pid, rss})
		}
	}
	sort.Slice(procs, func(i, j int) bool { return procs[i].rss > procs[j].rss })
	out := []string{}
	for i, p := range procs {
		if i >= limit {
			break
		}
		out = append(out, fmt.Sprintf("%s (pid %d) %s", p.name, p.pid, humanBytes(p.rss)))
	}
	return out
}
