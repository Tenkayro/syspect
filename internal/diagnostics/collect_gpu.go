package diagnostics

import (
	"context"
	"path/filepath"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/system"
)

var pciVendors = map[string]string{
	"0x1002": "AMD/ATI",
	"0x102b": "Matrox",
	"0x10de": "NVIDIA",
	"0x1234": "QEMU/Bochs",
	"0x15ad": "VMware",
	"0x1af4": "Red Hat Virtio",
	"0x8086": "Intel",
	"0x80ee": "VirtualBox",
}

type pciGraphicsAdapter struct {
	Address  string `json:"address"`
	Vendor   string `json:"vendor"`
	DeviceID string `json:"device_id"`
	Driver   string `json:"driver"`
	BootVGA  bool   `json:"boot_vga"`
}

func pciGraphicsAdapters() []pciGraphicsAdapter {
	out := []pciGraphicsAdapter{}
	for _, dir := range glob("/sys/bus/pci/devices/*") {
		class := strings.ToLower(read(dir + "/class"))
		if !strings.HasPrefix(class, "0x0300") && !strings.HasPrefix(class, "0x0302") && !strings.HasPrefix(class, "0x0380") {
			continue
		}
		vendorID := strings.ToLower(read(dir + "/vendor"))
		driver := ""
		if link, err := filepath.EvalSymlinks(dir + "/driver"); err == nil {
			driver = filepath.Base(link)
		}
		out = append(out, pciGraphicsAdapter{
			Address:  filepath.Base(dir),
			Vendor:   pciVendors[vendorID],
			DeviceID: strings.ToLower(read(dir + "/device")),
			Driver:   driver,
			BootVGA:  read(dir+"/boot_vga") == "1",
		})
	}
	return out
}

func drmConnectors() []map[string]any {
	out := []map[string]any{}
	for _, dir := range glob("/sys/class/drm/card[0-9]*-*") {
		status := read(dir + "/status")
		if status == "" {
			continue
		}
		out = append(out, map[string]any{
			"connector": filepath.Base(dir),
			"status":    status,
			"enabled":   read(dir+"/enabled"),
		})
	}
	return out
}

func gpuMemory() []map[string]any {
	out := []map[string]any{}
	for _, dir := range glob("/sys/class/drm/card[0-9]*/device") {
		vram, ok := readUint(dir + "/mem_info_vram_total")
		if !ok || vram == 0 {
			continue
		}
		gtt, _ := readUint(dir + "/mem_info_gtt_total")
		out = append(out, map[string]any{
			"card":       filepath.Base(filepath.Dir(dir)),
			"vram_bytes": vram,
			"vram_human": humanBytes(vram),
			"gtt_bytes":  gtt,
			"gtt_human":  humanBytes(gtt),
		})
	}
	return out
}

func pciDisplayRows(ctx context.Context, timeout time.Duration) []string {
	out, _, code := command(ctx, timeout, "lspci", "-mm")
	if code != 0 {
		return nil
	}
	rows := []string{}
	for _, line := range strings.Split(out, "\n") {
		ll := strings.ToLower(line)
		if strings.Contains(ll, "vga compatible") || strings.Contains(ll, "3d controller") || strings.Contains(ll, "display controller") {
			rows = append(rows, line)
		}
	}
	return rows
}

func collectGPU(ctx context.Context, c string, timeout time.Duration, cfg config.Config) []Result {
	results := []Result{}
	if adapters := pciGraphicsAdapters(); len(adapters) > 0 {
		results = append(results, pass(c, "Graphics adapters", adapters, "Detected from PCI class 0x0300/0x0302/0x0380 in /sys/bus/pci."))
	} else if rows := pciDisplayRows(ctx, timeout); len(rows) > 0 {
		results = append(results, pass(c, "Graphics adapters", rows, "Collected with lspci."))
	} else {
		results = append(results, skip(c, "Graphics adapters", "No PCI graphics devices were found."))
	}

	if connectors := drmConnectors(); len(connectors) > 0 {
		results = append(results, pass(c, "Display connectors", connectors, "Monitor connection state from DRM."))
	}
	if vram := gpuMemory(); len(vram) > 0 {
		results = append(results, pass(c, "GPU memory", vram, "VRAM reported by the amdgpu driver."))
	}
	if rows := pciDisplayRows(ctx, timeout); len(rows) > 0 {
		results = append(results, pass(c, "PCI details (lspci)", rows, "Optional verbose names when pciutils is installed."))
	}

	if system.Which("nvidia-smi") {
		o, e, k := command(ctx, timeout, "nvidia-smi", "--query-gpu=name,driver_version,memory.total,memory.used,temperature.gpu,utilization.gpu", "--format=csv,noheader,nounits")
		if k == 0 {
			results = append(results, pass(c, "NVIDIA telemetry", strings.TrimSpace(o), "Name, driver, memory (MiB), temperature (C), utilization (%)."))
		} else {
			results = append(results, warn(c, "NVIDIA telemetry", "Unavailable", e))
		}
	}
	return results
}
