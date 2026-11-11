package diagnostics

import (
	"fmt"
	"path/filepath"
	"sort"
	"strconv"
	"strings"

	"github.com/tenkayro/syspect/internal/config"
)

func collectKernel(c string, cfg config.Config) []Result {
	modules := []string{}
	for _, dir := range glob("/sys/module/*") {
		modules = append(modules, filepath.Base(dir))
		if len(modules) >= 80 {
			break
		}
	}
	totalModules := len(glob("/sys/module/*"))

	results := []Result{
		pass(c, "Kernel build", read("/proc/version"), ""),
		pass(c, "Loaded kernel modules", modules, fmt.Sprintf("First %d of %d modules exposed by /sys/module.", len(modules), totalModules)),
	}
	if tainted := kernelTaint(); tainted != "" {
		results = append(results, pass(c, "Kernel taint state", tainted, "A tainted kernel can indicate proprietary or out-of-tree code."))
	}
	if lockdown := read("/sys/kernel/security/lockdown"); lockdown != "" {
		results = append(results, pass(c, "Kernel lockdown", lockdown, ""))
	}
	if cfg.Privacy {
		results = append(results, skip(c, "Driver inventory", "Hidden in privacy mode."))
	}
	return results
}

func kernelTaint() string {
	raw := read("/proc/sys/kernel/tainted")
	value, err := strconv.ParseUint(strings.TrimSpace(raw), 10, 64)
	if err != nil {
		return ""
	}
	if value == 0 {
		return "Not tainted"
	}
	reasons := map[uint]string{
		0:  "proprietary module loaded",
		1:  "forced module load",
		2:  "kernel force-unloaded",
		3:  "machine check",
		4:  "bad page",
		5:  "user-requested taint",
		6:  "kernel oops",
		7:  "kernel die",
		8:  "ACPI table overridden",
		9:  "soft lockup",
		10: "live patch",
		11: "auxiliary taint",
		12: "staging driver",
		13: "technology preview",
		14: "firmware workaround",
		15: "out-of-tree module",
		16: "unsigned module",
	}
	found := []string{}
	for bit := uint(0); bit < 32; bit++ {
		if value&(1<<bit) == 0 {
			continue
		}
		if reason, ok := reasons[bit]; ok {
			found = append(found, reason)
		} else {
			found = append(found, fmt.Sprintf("bit %d set", bit))
		}
	}
	sort.Strings(found)
	return fmt.Sprintf("%s (%d)", strings.Join(found, "; "), value)
}
