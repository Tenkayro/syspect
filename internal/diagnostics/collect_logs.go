package diagnostics

import (
	"context"
	"io"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/system"
)

func collectLogs(ctx context.Context, c string, timeout time.Duration, cfg config.Config) []Result {
	if !cfg.Logs {
		return []Result{skip(c, "Recent system errors", "Log inspection is disabled by default.")}
	}
	if system.Which("journalctl") {
		o, e, k := command(ctx, timeout, "journalctl", "-p", "err", "-b", "--no-pager", "-n", "40", "-o", "short-iso")
		if k == 0 {
			lines := nonEmptyLines(o)
			if len(lines) > 30 {
				lines = lines[len(lines)-30:]
			}
			lines = collapseLines(lines)
			if cfg.Privacy {
				lines = []string{"Log contents hidden by privacy mode."}
			}
			if len(lines) == 0 {
				return []Result{pass(c, "Recent error-level journal entries", "No entries returned for current boot.", "")}
			}
			return []Result{warn(c, "Recent error-level journal entries", lines, "Error-priority journal entries were found; access may be incomplete.")}
		}
		return []Result{skip(c, "Journal inspection", e)}
	}

	for _, path := range []string{"/var/log/syslog", "/var/log/messages", "/var/log/kern.log"} {
		lines, err := tailMatching(path, 64*1024, 25)
		if err != nil {
			continue
		}
		if cfg.Privacy {
			return []Result{pass(c, "Recent system log errors", "Log contents hidden by privacy mode.", "Source: "+path)}
		}
		if len(lines) == 0 {
			return []Result{pass(c, "Recent system log errors", "No error-like entries in the tail of "+path, "")}
		}
		return []Result{warn(c, "Recent system log errors", collapseLines(lines), "Fallback scan of "+path+" because journalctl is unavailable.")}
	}
	return []Result{skip(c, "System log inspection", "Neither journalctl nor a readable /var/log file was found.")}
}

func collapseLines(lines []string) []string {
	order := []string{}
	counts := map[string]int{}
	for _, line := range lines {
		if counts[line] == 0 {
			order = append(order, line)
		}
		counts[line]++
	}
	out := make([]string, 0, len(order))
	for _, line := range order {
		if counts[line] > 1 {
			out = append(out, withCount(line, counts[line]))
			continue
		}
		out = append(out, line)
	}
	return out
}

func withCount(line string, n int) string {
	return line + " (x" + strconv.Itoa(n) + ")"
}

func tailMatching(path string, maxBytes int64, limit int) ([]string, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return nil, err
	}
	offset := info.Size() - maxBytes
	if offset < 0 {
		offset = 0
	}
	if _, err := f.Seek(offset, io.SeekStart); err != nil {
		return nil, err
	}
	data, err := io.ReadAll(f)
	if err != nil {
		return nil, err
	}
	out := []string{}
	for _, line := range strings.Split(string(data), "\n") {
		ll := strings.ToLower(line)
		if !strings.Contains(ll, "error") && !strings.Contains(ll, "fail") && !strings.Contains(ll, "warn") {
			continue
		}
		out = append(out, trimTo(line, 400))
	}
	if len(out) > limit {
		out = out[len(out)-limit:]
	}
	return out, nil
}
