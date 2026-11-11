package diagnostics

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/system"
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

var Categories = []Category{{"os", "Operating System"}, {"cpu", "CPU"}, {"memory", "Memory"}, {"gpu", "GPU & Graphics"}, {"board", "Motherboard & Firmware"}, {"storage", "Storage"}, {"network", "Network"}, {"sensors", "Temperatures & Sensors"}, {"battery", "Battery & Power"}, {"kernel", "Kernel & Drivers"}, {"services", "Services & Processes"}, {"logs", "System Logs"}, {"virtualization", "Virtualization & Containers"}}

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
		d := system.Distribution()
		host := read("/etc/hostname")
		if cfg.Privacy {
			host = "[REDACTED]"
		}
		up := read("/proc/uptime")
		if x := strings.Fields(up); len(x) > 0 {
			if f, e := strconv.ParseFloat(x[0], 64); e == nil {
				up = fmt.Sprintf("%.1f hours", f/3600)
			}
		}
		boot := "Legacy BIOS or unavailable"
		if _, e := os.Stat("/sys/firmware/efi"); e == nil {
			boot = "UEFI"
		}
		desktop := os.Getenv("XDG_CURRENT_DESKTOP")
		if desktop == "" {
			desktop = "Not detected"
		}
		return []Result{pass(c, "Distribution", d.Name, fmt.Sprintf("ID=%s; ID_LIKE=%s", d.ID, d.Like)), pass(c, "Distribution version", d.Version, "Codename: "+d.Codename), pass(c, "Kernel", read("/proc/sys/kernel/osrelease"), ""), pass(c, "Architecture", runtime.GOARCH, ""), pass(c, "Hostname", host, ""), pass(c, "Uptime", up, ""), pass(c, "Init system", system.Init(), ""), pass(c, "Boot mode", boot, ""), pass(c, "Desktop environment", desktop, ""), pass(c, "Go runtime", runtime.Version(), "")}
	case "cpu":
		info := read("/proc/cpuinfo")
		model := "Unknown"
		for _, l := range strings.Split(info, "\n") {
			if strings.HasPrefix(l, "model name") {
				if x := strings.SplitN(l, ":", 2); len(x) == 2 {
					model = strings.TrimSpace(x[1])
					break
				}
			}
		}
		n := runtime.NumCPU()
		freq := read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq")
		if x, e := strconv.Atoi(freq); e == nil {
			freq = fmt.Sprintf("%d MHz", x/1000)
		} else {
			freq = "Unavailable"
		}
		return []Result{pass(c, "Processor model", model, ""), pass(c, "Logical CPU count", n, ""), pass(c, "Current frequency", freq, ""), pass(c, "CPU virtualization flags", containsAny(info, "vmx", "svm"), "")}
	case "memory":
		vals := map[string]uint64{}
		for _, l := range strings.Split(read("/proc/meminfo"), "\n") {
			f := strings.Fields(l)
			if len(f) >= 2 {
				if n, e := strconv.ParseUint(f[1], 10, 64); e == nil {
					vals[strings.TrimSuffix(f[0], ":")] = n * 1024
				}
			}
		}
		total, avail := vals["MemTotal"], vals["MemAvailable"]
		if total == 0 {
			return []Result{skip(c, "RAM usage", "/proc/meminfo did not expose memory totals.")}
		}
		used := total - avail
		return []Result{pass(c, "RAM usage", map[string]any{"total_bytes": total, "used_bytes": used, "available_bytes": avail, "percent_used": float64(used) * 100 / float64(total)}, ""), pass(c, "Swap usage", map[string]any{"total_bytes": vals["SwapTotal"], "free_bytes": vals["SwapFree"]}, "")}
	case "gpu":
		out, err, code := command(ctx, timeout, "lspci", "-mm")
		if code != 0 {
			return []Result{skip(c, "Graphics adapters", "lspci unavailable: "+err)}
		}
		var rows []string
		for _, l := range strings.Split(out, "\n") {
			ll := strings.ToLower(l)
			if strings.Contains(ll, "vga compatible") || strings.Contains(ll, "3d controller") || strings.Contains(ll, "display controller") {
				rows = append(rows, l)
			}
		}
		r := []Result{pass(c, "Graphics adapters", rows, "")}
		if system.Which("nvidia-smi") {
			o, e, k := command(ctx, timeout, "nvidia-smi", "--query-gpu=name,driver_version,memory.total,memory.used,temperature.gpu,utilization.gpu", "--format=csv,noheader,nounits")
			if k == 0 {
				r = append(r, pass(c, "NVIDIA telemetry", strings.TrimSpace(o), "Name, driver, memory (MiB), temperature (C), utilization (%)."))
			} else {
				r = append(r, warn(c, "NVIDIA telemetry", "Unavailable", e))
			}
		} else {
			r = append(r, skip(c, "NVIDIA telemetry", "nvidia-smi is not installed; normal on non-NVIDIA systems."))
		}
		return r
	case "board":
		base := "/sys/class/dmi/id/"
		fields := map[string]string{"System manufacturer": "sys_vendor", "System model": "product_name", "Motherboard manufacturer": "board_vendor", "Motherboard model": "board_name", "BIOS vendor": "bios_vendor", "BIOS version": "bios_version", "BIOS date": "bios_date"}
		r := []Result{}
		for n, f := range fields {
			v := read(filepath.Join(base, f))
			if v == "" {
				r = append(r, skip(c, n, "DMI value unavailable or access restricted."))
			} else {
				r = append(r, pass(c, n, v, ""))
			}
		}
		if cfg.Privacy {
			r = append(r, skip(c, "Detailed firmware and board inventory", "Hidden in privacy mode because firmware tables may contain serial numbers."))
		} else if system.Which("dmidecode") {
			out, err, code := command(ctx, timeout, "dmidecode", "--type", "0,1,2")
			if code == 0 {
				r = append(r, pass(c, "Detailed firmware and board inventory", strings.TrimSpace(out), "Collected with dmidecode."))
			} else {
				r = append(r, skip(c, "Detailed firmware and board inventory", err+" (administrator privileges may be required)."))
			}
		}
		if system.Which("lsusb") {
			out, err, code := command(ctx, timeout, "lsusb")
			if code == 0 {
				r = append(r, pass(c, "USB devices", strings.TrimSpace(out), "USB device list."))
			} else {
				r = append(r, skip(c, "USB devices", err))
			}
		}
		return r
	case "storage":
		o, e, k := command(ctx, timeout, "lsblk", "--json", "--bytes", "--output", "NAME,TYPE,SIZE,MODEL,ROTA,FSTYPE,MOUNTPOINT,TRAN")
		if k != 0 {
			return []Result{skip(c, "Block devices", "lsblk unavailable: "+e)}
		}
		var data any
		if json.Unmarshal([]byte(o), &data) != nil {
			return []Result{warn(c, "Block devices", "Unparseable lsblk JSON", "")}
		}
		r := []Result{pass(c, "Block devices", data, "Collected with lsblk.")}
		df, er, code := command(ctx, timeout, "df", "-P", "-T", "-x", "tmpfs", "-x", "devtmpfs")
		if code == 0 {
			r = append(r, pass(c, "Mounted filesystem usage", strings.TrimSpace(df), ""))
		} else {
			r = append(r, skip(c, "Filesystem usage", er))
		}
		if !cfg.Smart {
			r = append(r, skip(c, "SMART health", "SMART inspection is disabled in settings."))
		} else if !system.Which("smartctl") {
			r = append(r, skip(c, "SMART health", "smartmontools (smartctl) is not installed."))
		} else {
			var root map[string]any
			_ = json.Unmarshal([]byte(o), &root)
			devices, _ := root["blockdevices"].([]any)
			checked := 0
			for _, raw := range devices {
				device, ok := raw.(map[string]any)
				if !ok || device["type"] != "disk" {
					continue
				}
				name, _ := device["name"].(string)
				if name == "" || checked >= 12 {
					continue
				}
				checked++
				path := name
				if !strings.HasPrefix(path, "/dev/") {
					path = "/dev/" + path
				}
				so, se, sc := command(ctx, timeout, "smartctl", "-a", "-j", path)
				var smart map[string]any
				if json.Unmarshal([]byte(so), &smart) != nil {
					r = append(r, skip(c, "SMART health: "+name, se))
					continue
				}
				status := "Health status unavailable"
				if ss, ok := smart["smart_status"].(map[string]any); ok {
					if passed, ok := ss["passed"].(bool); ok {
						if passed {
							status = "PASSED"
						} else {
							status = "FAILED"
						}
					}
				}
				if status == "FAILED" {
					r = append(r, warn(c, "SMART health: "+name, status, "The drive reports a SMART health failure."))
				} else if status == "PASSED" {
					r = append(r, pass(c, "SMART health: "+name, status, ""))
				} else {
					r = append(r, skip(c, "SMART health: "+name, se))
				}
				if temp, ok := smart["temperature"].(map[string]any); ok && temp["current"] != nil {
					r = append(r, pass(c, "Drive temperature: "+name, temp["current"], "Temperature reported by smartctl."))
				}
				_ = sc
			}
			if checked == 0 {
				r = append(r, skip(c, "SMART health", "No physical block devices were identified."))
			}
		}
		return r
	case "network":
		o, e, k := command(ctx, timeout, "ip", "-j", "address")
		if k != 0 {
			return []Result{skip(c, "Network interfaces", "ip unavailable: "+e)}
		}
		var data any
		_ = json.Unmarshal([]byte(o), &data)
		if cfg.Privacy { // Keep addresses out of the report entirely in privacy mode.
			data = "Network interface details hidden by privacy mode."
		}
		r := []Result{pass(c, "Network interfaces", data, "")}
		dns := []string{}
		for _, l := range strings.Split(read("/etc/resolv.conf"), "\n") {
			f := strings.Fields(l)
			if len(f) > 1 && f[0] == "nameserver" {
				if cfg.Privacy {
					dns = append(dns, "[REDACTED]")
				} else {
					dns = append(dns, f[1])
				}
			}
		}
		r = append(r, pass(c, "Configured DNS servers", dns, ""))
		if cfg.Network {
			o, er, k := command(ctx, timeout, "getent", "ahosts", "example.com")
			if k == 0 {
				r = append(r, pass(c, "DNS resolution test", strings.Split(strings.TrimSpace(o), "\n")[0], "Opt-in connectivity check."))
			} else {
				r = append(r, warn(c, "DNS resolution test", "Failed", er))
			}
		} else {
			r = append(r, skip(c, "Connectivity test", "Connectivity checks are opt-in and disabled."))
		}
		return r
	case "sensors":
		if !system.Which("sensors") {
			return []Result{skip(c, "Sensor readings", "lm-sensors (sensors) is not installed.")}
		}
		o, e, k := command(ctx, timeout, "sensors", "-j")
		if k == 0 {
			var v any
			if json.Unmarshal([]byte(o), &v) == nil {
				return []Result{pass(c, "Available sensor readings", v, "Raw structured readings from lm-sensors.")}
			}
		}
		return []Result{warn(c, "Sensor readings", "Unavailable", e)}
	case "battery":
		entries, _ := filepath.Glob("/sys/class/power_supply/*")
		var vals []map[string]string
		for _, p := range entries {
			if read(p+"/type") == "Battery" {
				v := map[string]string{"device": filepath.Base(p)}
				for _, f := range []string{"manufacturer", "model_name", "status", "capacity", "health", "technology"} {
					if x := read(p + "/" + f); x != "" {
						v[f] = x
					}
				}
				vals = append(vals, v)
			}
		}
		if len(vals) == 0 {
			return []Result{skip(c, "Battery information", "No battery detected; normal for many desktops and servers.")}
		}
		return []Result{pass(c, "Battery status", vals, "")}
	case "kernel":
		modules, _ := filepath.Glob("/sys/module/*")
		names := []string{}
		for _, p := range modules {
			names = append(names, filepath.Base(p))
			if len(names) >= 80 {
				break
			}
		}
		secure := "Not available (legacy boot or restricted environment)"
		if _, e := os.Stat("/sys/firmware/efi/efivars"); e == nil {
			secure = "Present; exact state may require efivarfs permissions"
		}
		return []Result{pass(c, "Loaded kernel modules", names, "First 80 modules exposed by /sys/module."), pass(c, "Secure Boot status", secure, "")}
	case "services":
		r := []Result{}
		if system.Init() == "systemd" && system.Which("systemctl") {
			o, e, k := command(ctx, timeout, "systemctl", "--failed", "--no-legend", "--plain")
			if k == 0 {
				lines := []string{}
				for _, l := range strings.Split(o, "\n") {
					if strings.TrimSpace(l) != "" {
						lines = append(lines, l)
					}
				}
				if len(lines) == 0 {
					r = append(r, pass(c, "Failed systemd services", "None reported.", ""))
				} else {
					r = append(r, warn(c, "Failed systemd services", lines, "Failed units were reported."))
				}
			} else {
				r = append(r, skip(c, "Failed systemd services", e))
			}
		} else {
			r = append(r, skip(c, "Failed systemd services", "systemd is not the detected init system."))
		}
		if system.Which("ps") {
			o, _, k := command(ctx, timeout, "ps", "-eo", "pid,comm,pcpu,pmem", "--sort=-pcpu")
			if k == 0 {
				lines := strings.Split(strings.TrimSpace(o), "\n")
				if len(lines) > 7 {
					lines = lines[:7]
				}
				r = append(r, pass(c, "Top CPU processes", lines, "Snapshot; full command lines are not collected."))
			}
		}
		return r
	case "logs":
		if !cfg.Logs {
			return []Result{skip(c, "Recent system errors", "Log inspection is disabled by default.")}
		}
		if system.Which("journalctl") {
			o, e, k := command(ctx, timeout, "journalctl", "-p", "err", "-b", "--no-pager", "-n", "40", "-o", "short-iso")
			if k == 0 {
				lines := []string{}
				for _, l := range strings.Split(o, "\n") {
					if strings.TrimSpace(l) != "" {
						lines = append(lines, l)
					}
				}
				if len(lines) > 30 {
					lines = lines[len(lines)-30:]
				}
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
		return []Result{skip(c, "System log inspection", "journalctl is unavailable.")}
	case "virtualization":
		r := []Result{}
		if system.Which("systemd-detect-virt") {
			o, _, _ := command(ctx, timeout, "systemd-detect-virt", "--vm")
			if strings.TrimSpace(o) == "" {
				o = "none detected"
			}
			r = append(r, pass(c, "Virtual machine", strings.TrimSpace(o), ""))
			o, _, _ = command(ctx, timeout, "systemd-detect-virt", "--container")
			if strings.TrimSpace(o) == "" {
				o = "none detected"
			}
			r = append(r, pass(c, "Container environment", strings.TrimSpace(o), ""))
		} else {
			r = append(r, skip(c, "Virtualization detection", "systemd-detect-virt unavailable."))
		}
		found := []string{}
		for _, x := range []string{"docker", "podman", "lxc", "virsh"} {
			if system.Which(x) {
				found = append(found, x)
			}
		}
		return append(r, pass(c, "Container and VM management tools", found, ""))
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
