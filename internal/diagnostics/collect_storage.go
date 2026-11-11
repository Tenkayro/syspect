package diagnostics

import (
	"context"
	"encoding/json"
	"path/filepath"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/system"
)

type blockDevice struct {
	Name       string   `json:"name"`
	Size       uint64   `json:"size_bytes"`
	SizeHuman  string   `json:"size_human"`
	Model      string   `json:"model,omitempty"`
	Rotational bool     `json:"rotational"`
	Removable  bool     `json:"removable"`
	ReadOnly   bool     `json:"read_only"`
	Serial     string   `json:"serial,omitempty"`
	Partitions []string `json:"partitions,omitempty"`
}

func blockDevices(cfg config.Config) []blockDevice {
	out := []blockDevice{}
	for _, dir := range glob("/sys/block/*") {
		name := filepath.Base(dir)
		if strings.HasPrefix(name, "loop") || strings.HasPrefix(name, "ram") {
			continue
		}
		sectors, _ := readUint(dir + "/size")
		size := sectors * 512
		dev := blockDevice{
			Name:       name,
			Size:       size,
			SizeHuman:  humanBytes(size),
			Model:      read(dir + "/device/model"),
			Rotational: read(dir+"/queue/rotational") == "1",
			Removable:  read(dir+"/removable") == "1",
			ReadOnly:   read(dir+"/ro") == "1",
		}
		if !cfg.Privacy {
			dev.Serial = read(dir + "/device/serial")
		}
		if dev.Model == "" {
			dev.Model = read(dir + "/device/name")
		}
		parts := []string{}
		for _, part := range glob(dir + "/" + name + "*") {
			p := filepath.Base(part)
			if p == name {
				continue
			}
			parts = append(parts, p)
		}
		dev.Partitions = parts
		out = append(out, dev)
	}
	return out
}

func mounts() []map[string]string {
	out := []map[string]string{}
	skipTypes := map[string]bool{
		"proc": true, "sysfs": true, "devtmpfs": true, "devpts": true,
		"cgroup": true, "cgroup2": true, "pstore": true, "securityfs": true,
		"debugfs": true, "tracefs": true, "configfs": true, "fusectl": true,
		"mqueue": true, "bpf": true, "hugetlbfs": true, "autofs": true,
	}
	for _, line := range strings.Split(read("/proc/mounts"), "\n") {
		f := strings.Fields(line)
		if len(f) < 4 || skipTypes[f[2]] {
			continue
		}
		out = append(out, map[string]string{
			"device":     f[0],
			"mountpoint": f[1],
			"filesystem": f[2],
			"options":    f[3],
		})
		if len(out) >= 40 {
			break
		}
	}
	return out
}

func nvmeControllers(cfg config.Config) []map[string]string {
	out := []map[string]string{}
	for _, dir := range glob("/sys/class/nvme/nvme*") {
		model := read(dir + "/model")
		state := read(dir + "/state")
		if model == "" && state == "" {
			continue
		}
		entry := map[string]string{
			"controller": filepath.Base(dir),
			"model":      model,
			"firmware":   read(dir + "/firmware_rev"),
			"state":      state,
			"transport":  read(dir + "/transport"),
			"address":    read(dir + "/address"),
		}
		if !cfg.Privacy {
			entry["serial"] = read(dir + "/serial")
		}
		out = append(out, entry)
	}
	return out
}

func collectStorage(ctx context.Context, c string, timeout time.Duration, cfg config.Config) []Result {
	devices := blockDevices(cfg)
	results := []Result{}
	if len(devices) > 0 {
		results = append(results, pass(c, "Block devices", devices, "Inventory from /sys/block, including partitions and rotation type."))
	} else {
		results = append(results, skip(c, "Block devices", "No block devices were exposed by the kernel."))
	}

	if nvme := nvmeControllers(cfg); len(nvme) > 0 {
		results = append(results, pass(c, "NVMe controllers", nvme, "Controller model, firmware and link state from /sys/class/nvme."))
	}

	if m := mounts(); len(m) > 0 {
		results = append(results, pass(c, "Mounted filesystems", m, "Entries from /proc/mounts without virtual filesystems."))
	}

	if df, err, code := command(ctx, timeout, "df", "-P", "-T", "-x", "tmpfs", "-x", "devtmpfs"); code == 0 {
		results = append(results, pass(c, "Filesystem usage", strings.TrimSpace(df), "Output of df -P -T."))
	} else {
		results = append(results, skip(c, "Filesystem usage", err))
	}

	results = append(results, smartResults(ctx, c, timeout, cfg, devices)...)
	return results
}

func smartResults(ctx context.Context, c string, timeout time.Duration, cfg config.Config, devices []blockDevice) []Result {
	if !cfg.Smart {
		return []Result{skip(c, "SMART health", "SMART inspection is disabled in settings.")}
	}
	if !system.Which("smartctl") {
		return []Result{skip(c, "SMART health", "smartmontools (smartctl) is not installed.")}
	}
	results := []Result{}
	checked := 0
	for _, dev := range devices {
		if checked >= 12 {
			break
		}
		if dev.Removable || strings.HasPrefix(dev.Name, "zram") || strings.HasPrefix(dev.Name, "dm-") {
			continue
		}
		checked++
		path := "/dev/" + dev.Name
		so, se, _ := command(ctx, timeout, "smartctl", "-a", "-j", path)
		var smart map[string]any
		if json.Unmarshal([]byte(so), &smart) != nil {
			results = append(results, skip(c, "SMART health: "+dev.Name, se))
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
		switch status {
		case "FAILED":
			results = append(results, warn(c, "SMART health: "+dev.Name, status, "The drive reports a SMART health failure."))
		case "PASSED":
			results = append(results, pass(c, "SMART health: "+dev.Name, status, ""))
		default:
			results = append(results, skip(c, "SMART health: "+dev.Name, se))
		}
		if temp, ok := smart["temperature"].(map[string]any); ok && temp["current"] != nil {
			results = append(results, pass(c, "Drive temperature: "+dev.Name, temp["current"], "Temperature reported by smartctl."))
		}
	}
	if checked == 0 {
		results = append(results, skip(c, "SMART health", "No physical block devices were identified."))
	}
	return results
}
