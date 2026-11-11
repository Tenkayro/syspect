package diagnostics

import (
	"fmt"
	"os"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/system"
)

func collectOS(c string, cfg config.Config) []Result {
	d := system.Distribution()
	host, _ := os.Hostname()
	if cfg.Privacy {
		host = "[REDACTED]"
	}
	desktop := os.Getenv("XDG_CURRENT_DESKTOP")
	if desktop == "" {
		desktop = os.Getenv("DESKTOP_SESSION")
	}
	if desktop == "" {
		desktop = "Not detected"
	}
	return []Result{
		pass(c, "Distribution", d.Name, fmt.Sprintf("ID=%s; ID_LIKE=%s", d.ID, d.Like)),
		pass(c, "Distribution version", d.Version, "Codename: "+d.Codename),
		pass(c, "Kernel", read("/proc/sys/kernel/osrelease"), firstLine(read("/proc/version"))),
		pass(c, "Architecture", runtime.GOARCH, ""),
		pass(c, "Hostname", host, ""),
		pass(c, "Uptime", systemUptime(), ""),
		pass(c, "Boot time", bootTime(), ""),
		pass(c, "Init system", system.Init(), ""),
		pass(c, "Boot mode", bootMode(), ""),
		pass(c, "Desktop environment", desktop, ""),
		pass(c, "Kernel command line", read("/proc/cmdline"), "Options the running kernel was booted with."),
		pass(c, "Load average", read("/proc/loadavg"), "1, 5 and 15 minute load averages plus scheduling entities."),
		pass(c, "Go runtime", runtime.Version(), ""),
	}
}

func systemUptime() string {
	f := strings.Fields(read("/proc/uptime"))
	if len(f) == 0 {
		return "Unavailable"
	}
	sec, err := strconv.ParseFloat(f[0], 64)
	if err != nil {
		return "Unavailable"
	}
	return humanDuration(time.Duration(sec) * time.Second)
}

func bootTime() string {
	for _, line := range strings.Split(read("/proc/stat"), "\n") {
		if !strings.HasPrefix(line, "btime ") {
			continue
		}
		n, err := strconv.ParseInt(strings.TrimSpace(strings.TrimPrefix(line, "btime ")), 10, 64)
		if err != nil {
			break
		}
		return time.Unix(n, 0).Format("2006-01-02 15:04:05 MST")
	}
	return "Unavailable"
}

func bootMode() string {
	if _, err := os.Stat("/sys/firmware/efi"); err != nil {
		return "Legacy BIOS or unavailable"
	}
	if sb, ok := secureBootState(); ok {
		if sb {
			return "UEFI (Secure Boot enabled)"
		}
		return "UEFI (Secure Boot disabled)"
	}
	return "UEFI"
}
