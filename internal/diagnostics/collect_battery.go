package diagnostics

import (
	"fmt"
	"path/filepath"

	"github.com/tenkayro/syspect/internal/config"
)

func collectBattery(c string, cfg config.Config) []Result {
	batteries := []map[string]any{}
	ac := []map[string]any{}
	for _, dir := range glob("/sys/class/power_supply/*") {
		name := filepath.Base(dir)
		switch read(dir + "/type") {
		case "Battery":
			entry := map[string]any{"device": name}
			for _, field := range []string{"manufacturer", "model_name", "status", "capacity", "capacity_level", "health", "technology", "cycle_count"} {
				if v := read(dir + "/" + field); v != "" {
					entry[field] = v
				}
			}
			if !cfg.Privacy {
				if v := read(dir + "/serial_number"); v != "" {
					entry["serial_number"] = v
				}
			}
			if v, ok := readInt(dir + "/voltage_now"); ok && v > 0 {
				entry["voltage_v"] = float64(v) / 1e6
			}
			if v, ok := readInt(dir + "/power_now"); ok && v != 0 {
				entry["power_w"] = float64(v) / 1e6
			}
			for _, f := range []string{"energy_now", "energy_full", "energy_full_design"} {
				if v, ok := readInt(dir + "/" + f); ok && v > 0 {
					entry[f+"_wh"] = fmt.Sprintf("%.1f Wh", float64(v)/1e6)
				}
			}
			for _, f := range []string{"charge_now", "charge_full", "charge_full_design"} {
				if v, ok := readInt(dir + "/" + f); ok && v > 0 {
					entry[f+"_ah"] = fmt.Sprintf("%.2f Ah", float64(v)/1e6)
				}
			}
			if pct := batteryHealth(dir); pct > 0 {
				entry["health_percent"] = fmt.Sprintf("%.1f%%", pct)
			}
			batteries = append(batteries, entry)
		case "Mains", "AC", "USB":
			if read(dir+"/online") != "" {
				ac = append(ac, map[string]any{"device": name, "online": read(dir+"/online")})
			}
		}
	}
	results := []Result{}
	if len(batteries) > 0 {
		results = append(results, pass(c, "Battery status", batteries, "Charge, health and power draw from /sys/class/power_supply."))
	} else {
		results = append(results, skip(c, "Battery information", "No battery detected; normal for many desktops and servers."))
	}
	if len(ac) > 0 {
		results = append(results, pass(c, "AC adapters", ac, "Power supply online state."))
	}
	return results
}

func batteryHealth(dir string) float64 {
	full, okFull := readInt(dir + "/energy_full")
	design, okDesign := readInt(dir + "/energy_full_design")
	if !okFull || !okDesign || design <= 0 {
		full, okFull = readInt(dir + "/charge_full")
		design, okDesign = readInt(dir + "/charge_full_design")
	}
	if !okFull || !okDesign || design <= 0 {
		return 0
	}
	return float64(full) * 100 / float64(design)
}
