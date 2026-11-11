package diagnostics

import (
	"fmt"
	"path/filepath"
	"runtime"
	"sort"
	"strings"

	"github.com/tenkayro/syspect/internal/config"
)

func collectCPU(c string, cfg config.Config) []Result {
	info := read("/proc/cpuinfo")
	physical := physicalCoreCount(info)
	logical := runtime.NumCPU()
	base := "/sys/devices/system/cpu/cpu0"

	results := []Result{
		pass(c, "Processor model", cpuModel(info), ""),
		pass(c, "Physical cores", coresOrUnavailable(physical), "Unique package/core pairs from /proc/cpuinfo."),
		pass(c, "Logical CPU count", logical, ""),
		pass(c, "Threads per core", threadSummary(physical, logical), ""),
	}

	if khz, ok := readInt(base + "/cpufreq/scaling_cur_freq"); ok && khz > 0 {
		results = append(results, pass(c, "Current frequency", fmt.Sprintf("%d MHz", khz/1000), ""))
	}
	maxKhz, haveMax := readInt(base + "/cpufreq/cpuinfo_max_freq")
	if !haveMax {
		maxKhz, haveMax = readInt(base + "/cpufreq/scaling_max_freq")
	}
	if haveMax && maxKhz > 0 {
		results = append(results, pass(c, "Maximum frequency", fmt.Sprintf("%d MHz", maxKhz/1000), ""))
	}
	if governor := read(base + "/cpufreq/scaling_governor"); governor != "" {
		results = append(results, pass(c, "Scaling governor", governor, "CPU frequency policy selected by the kernel."))
	}
	if microcode := cpuMicrocode(info); microcode != "" {
		results = append(results, pass(c, "Microcode revision", microcode, ""))
	}

	if caches := cpuCaches(); len(caches) > 0 {
		results = append(results, pass(c, "CPU caches", caches, "Cache hierarchy reported by the kernel for CPU 0."))
	}
	if temps := cpuTemperatures(); len(temps) > 0 {
		results = append(results, pass(c, "CPU temperature", temps, "Read directly from /sys/class/hwmon."))
	}

	results = append(results, pass(c, "CPU features", containsAny(info, "vmx", "svm", "aes", "avx", "avx2", "avx512f", "fma", "bmi2", "sha_ni", "rdrand"), "Virtualization, encryption, vector and hashing instruction support."))

	if vulns := cpuVulnerabilities(); len(vulns) > 0 {
		status := "PASS"
		severity := "info"
		for _, v := range vulns {
			if strings.HasPrefix(v, "Vulnerable") {
				status = "WARNING"
				severity = "warning"
			}
		}
		results = append(results, Result{c, "CPU vulnerability mitigations", status, vulns, "Kernel-reported mitigation state per CPU vulnerability.", "", severity})
	}

	if nodes := len(glob("/sys/devices/system/node/node[0-9]*")); nodes > 0 {
		results = append(results, pass(c, "NUMA nodes", nodes, ""))
	}
	return results
}

func cpuModel(info string) string {
	for _, line := range strings.Split(info, "\n") {
		key := strings.TrimSpace(strings.SplitN(line, ":", 2)[0])
		switch key {
		case "model name", "Model", "Hardware", "Processor":
			if parts := strings.SplitN(line, ":", 2); len(parts) == 2 {
				if v := strings.TrimSpace(parts[1]); v != "" {
					return v
				}
			}
		}
	}
	return "Unknown"
}

func cpuMicrocode(info string) string {
	value := ""
	if v := read("/sys/devices/system/cpu/cpu0/microcode/version"); v != "" {
		value = v
	} else if v := read("/sys/devices/system/cpu/microcode/version"); v != "" {
		value = v
	}
	if value == "" {
		for _, line := range strings.Split(info, "\n") {
			if strings.HasPrefix(line, "microcode") {
				if parts := strings.SplitN(line, ":", 2); len(parts) == 2 {
					value = strings.TrimSpace(parts[1])
				}
			}
		}
	}
	if value == "" || value == "0xffffffff" || value == "0x0" {
		return ""
	}
	return value
}

// physicalCoreCount counts unique (physical id, core id) pairs in /proc/cpuinfo.
func physicalCoreCount(info string) int {
	seen := map[string]bool{}
	physical, core := "", ""
	flush := func() {
		if core != "" {
			seen[physical+"/"+core] = true
		}
		physical, core = "", ""
	}
	for _, line := range strings.Split(info, "\n") {
		if strings.TrimSpace(line) == "" {
			flush()
			continue
		}
		parts := strings.SplitN(line, ":", 2)
		if len(parts) != 2 {
			continue
		}
		switch strings.TrimSpace(parts[0]) {
		case "physical id":
			physical = strings.TrimSpace(parts[1])
		case "core id":
			core = strings.TrimSpace(parts[1])
		}
	}
	flush()
	return len(seen)
}

func coresOrUnavailable(n int) any {
	if n <= 0 {
		return "Unavailable"
	}
	return n
}

func threadSummary(physical, logical int) string {
	if physical <= 0 {
		return "Unavailable"
	}
	perCore := float64(logical) / float64(physical)
	if perCore == float64(int(perCore)) {
		return fmt.Sprintf("%d logical / %d physical (%d per core)", logical, physical, int(perCore))
	}
	return fmt.Sprintf("%d logical / %d physical (%.1f per core)", logical, physical, perCore)
}

func cpuCaches() []string {
	out := []string{}
	for _, dir := range glob("/sys/devices/system/cpu/cpu0/cache/index*") {
		level := read(dir + "/level")
		kind := read(dir + "/type")
		size := read(dir + "/size")
		if level == "" || size == "" {
			continue
		}
		label := "L" + level
		switch strings.ToLower(kind) {
		case "data":
			label += "d"
		case "instruction":
			label += "i"
		case "unified":
		}
		out = append(out, label+" "+cacheSize(size))
	}
	return out
}

func cacheSize(s string) string {
	if strings.HasSuffix(s, "K") {
		return strings.TrimSuffix(s, "K") + " KiB"
	}
	if strings.HasSuffix(s, "M") {
		return strings.TrimSuffix(s, "M") + " MiB"
	}
	return s
}

func cpuTemperatures() []string {
	out := []string{}
	for _, chip := range hwmonChips() {
		switch chip.Name {
		case "coretemp", "k10temp", "zenpower", "cpu_thermal", "soc_thermal":
		default:
			continue
		}
		for _, r := range chip.Temps() {
			out = append(out, fmt.Sprintf("%s: %s", r.Label, r.Value))
		}
	}
	return out
}

func cpuVulnerabilities() map[string]string {
	out := map[string]string{}
	for _, p := range glob("/sys/devices/system/cpu/vulnerabilities/*") {
		name := strings.ReplaceAll(filepath.Base(p), "_", " ")
		if v := read(p); v != "" {
			out[name] = v
		}
	}
	if len(out) == 0 {
		return nil
	}
	// Sort inside a map is impossible; callers marshal deterministically enough.
	keys := make([]string, 0, len(out))
	for k := range out {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	ordered := map[string]string{}
	for _, k := range keys {
		ordered[k] = out[k]
	}
	return ordered
}
