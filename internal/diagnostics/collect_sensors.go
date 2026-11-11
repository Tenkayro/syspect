package diagnostics

import (
	"context"
	"fmt"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/system"
)

type hwmonChip struct {
	Dir  string
	Name string
}

type hwmonValue struct {
	Label string
	Value string
}

func hwmonChips() []hwmonChip {
	out := []hwmonChip{}
	for _, dir := range glob("/sys/class/hwmon/hwmon*") {
		name := read(dir + "/name")
		if name == "" {
			name = filepath.Base(dir)
		}
		out = append(out, hwmonChip{Dir: dir, Name: name})
	}
	return out
}

func (c hwmonChip) Temps() []hwmonValue {
	out := []hwmonValue{}
	for _, input := range glob(c.Dir + "/temp*_input") {
		milli, err := strconv.ParseInt(strings.TrimSpace(read(input)), 10, 64)
		if err != nil {
			continue
		}
		base := strings.TrimSuffix(input, "_input")
		label := read(base + "_label")
		if label == "" {
			label = strings.TrimPrefix(filepath.Base(base), "temp")
			label = "temp" + label
		}
		out = append(out, hwmonValue{Label: label, Value: fmt.Sprintf("%.1f °C", float64(milli)/1000)})
	}
	return out
}

func (c hwmonChip) Fans() []hwmonValue {
	out := []hwmonValue{}
	for _, input := range glob(c.Dir + "/fan*_input") {
		rpm, err := strconv.ParseInt(strings.TrimSpace(read(input)), 10, 64)
		if err != nil || rpm == 0 {
			continue
		}
		base := strings.TrimSuffix(input, "_input")
		label := read(base + "_label")
		if label == "" {
			label = "fan" + strings.TrimPrefix(filepath.Base(base), "fan")
		}
		out = append(out, hwmonValue{Label: label, Value: fmt.Sprintf("%d RPM", rpm)})
	}
	return out
}

func (c hwmonChip) scaled(prefix, label string, scale float64, unit string) []hwmonValue {
	out := []hwmonValue{}
	for _, input := range glob(c.Dir + "/" + prefix + "*_input") {
		raw, err := strconv.ParseInt(strings.TrimSpace(read(input)), 10, 64)
		if err != nil {
			continue
		}
		base := strings.TrimSuffix(input, "_input")
		name := read(base + "_label")
		if name == "" {
			name = prefix + strings.TrimPrefix(filepath.Base(base), prefix)
		}
		out = append(out, hwmonValue{Label: name, Value: fmt.Sprintf("%.2f %s", float64(raw)*scale, unit)})
	}
	return out
}

type sensorReading struct {
	Chip  string `json:"chip"`
	Kind  string `json:"kind"`
	Label string `json:"label"`
	Value string `json:"value"`
}

func collectSensors(ctx context.Context, c string, timeout time.Duration) []Result {
	readings := []sensorReading{}
	for _, chip := range hwmonChips() {
		for _, v := range chip.Temps() {
			readings = append(readings, sensorReading{chip.Name, "temperature", v.Label, v.Value})
		}
		for _, v := range chip.Fans() {
			readings = append(readings, sensorReading{chip.Name, "fan", v.Label, v.Value})
		}
		for _, v := range chip.scaled("in", "voltage", 0.001, "V") {
			readings = append(readings, sensorReading{chip.Name, "voltage", v.Label, v.Value})
		}
		for _, v := range chip.scaled("power", "power", 0.000001, "W") {
			readings = append(readings, sensorReading{chip.Name, "power", v.Label, v.Value})
		}
		if len(readings) >= 150 {
			break
		}
	}

	zones := []map[string]any{}
	for _, dir := range glob("/sys/class/thermal/thermal_zone*") {
		milli, err := strconv.ParseInt(strings.TrimSpace(read(dir+"/temp")), 10, 64)
		if err != nil {
			continue
		}
		zones = append(zones, map[string]any{
			"zone":       filepath.Base(dir),
			"type":       read(dir + "/type"),
			"temp_c":     float64(milli) / 1000,
			"temp_human": fmt.Sprintf("%.1f °C", float64(milli)/1000),
		})
	}

	results := []Result{}
	if len(readings) > 0 {
		results = append(results, pass(c, "Hardware sensors", readings, "Temperatures, fans, voltages and power read directly from /sys/class/hwmon."))
	}
	if len(zones) > 0 {
		results = append(results, pass(c, "Thermal zones", zones, "ACPI/SoC thermal zones exposed by the kernel."))
	}

	if len(results) == 0 {
		if system.Which("sensors") {
			out, err, code := command(ctx, timeout, "sensors", "-j")
			if code == 0 {
				return []Result{pass(c, "Sensor readings", out, "Raw structured readings from lm-sensors.")}
			}
			return []Result{warn(c, "Sensor readings", "Unavailable", err)}
		}
		return []Result{skip(c, "Sensor readings", "No hwmon or thermal sensors were exposed by the kernel.")}
	}
	return results
}
