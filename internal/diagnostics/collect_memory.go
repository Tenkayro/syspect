package diagnostics

import (
	"path/filepath"
	"strconv"
	"strings"

	"github.com/tenkayro/syspect/internal/config"
)

func collectMemory(c string, cfg config.Config) []Result {
	vals := map[string]uint64{}
	for _, line := range strings.Split(read("/proc/meminfo"), "\n") {
		f := strings.Fields(line)
		if len(f) < 2 {
			continue
		}
		if n, err := strconv.ParseUint(f[1], 10, 64); err == nil {
			vals[strings.TrimSuffix(f[0], ":")] = n * 1024
		}
	}
	total, avail := vals["MemTotal"], vals["MemAvailable"]
	if total == 0 {
		return []Result{skip(c, "RAM usage", "/proc/meminfo did not expose memory totals.")}
	}
	cached := vals["Cached"] + vals["SReclaimable"]
	used := total - avail
	swapTotal, swapFree := vals["SwapTotal"], vals["SwapFree"]

	results := []Result{
		pass(c, "RAM usage", map[string]any{
			"total_bytes":     total,
			"used_bytes":      used,
			"available_bytes": avail,
			"percent_used":    float64(used) * 100 / float64(total),
			"total_human":     humanBytes(total),
			"used_human":      humanBytes(used),
			"available_human": humanBytes(avail),
		}, ""),
		pass(c, "Memory breakdown", map[string]any{
			"free_bytes":   vals["MemFree"],
			"cached_bytes": cached,
			"buffers_bytes": vals["Buffers"],
			"dirty_bytes":  vals["Dirty"],
			"slab_bytes":   vals["Slab"],
		}, "Cached includes reclaimable slab memory."),
	}

	if swapTotal > 0 {
		swapUsed := swapTotal - swapFree
		results = append(results, pass(c, "Swap usage", map[string]any{
			"total_bytes":  swapTotal,
			"used_bytes":   swapUsed,
			"free_bytes":   swapFree,
			"percent_used": float64(swapUsed) * 100 / float64(swapTotal),
			"total_human":  humanBytes(swapTotal),
			"used_human":   humanBytes(swapUsed),
		}, ""))
	} else {
		results = append(results, pass(c, "Swap usage", "No swap configured", "Normal for systems with enough RAM or zram-only setups."))
	}

	if hp := vals["HugePages_Total"]; hp > 0 {
		results = append(results, pass(c, "Huge pages", map[string]any{
			"total": vals["HugePages_Total"],
			"free":  vals["HugePages_Free"],
			"size":  humanBytes(vals["Hugepagesize"]),
		}, ""))
	}
	if dimms := edacDimms(); len(dimms) > 0 {
		results = append(results, pass(c, "Memory modules (EDAC)", dimms, "Reported by the kernel EDAC subsystem; labels depend on the platform."))
	}
	return results
}

type edacDimm struct {
	Label string `json:"label"`
	Size  string `json:"size"`
	Type  string `json:"type"`
}

func edacDimms() []edacDimm {
	out := []edacDimm{}
	for _, ctrl := range glob("/sys/devices/system/edac/mc/mc*") {
		for _, dimm := range glob(ctrl + "/dimm*") {
			label := read(dimm + "/dimm_label")
			if label == "" {
				label = filepath.Base(dimm)
			}
			size := read(dimm + "/size")
			if n, err := strconv.ParseUint(size, 10, 64); err == nil && n > 0 {
				size = humanBytes(n * 1024 * 1024)
			}
			kind := read(dimm + "/dimm_mem_type")
			if size == "" && kind == "" && read(dimm+"/dimm_location") == "" {
				continue
			}
			out = append(out, edacDimm{Label: label, Size: size, Type: kind})
			if len(out) >= 32 {
				return out
			}
		}
	}
	return out
}
