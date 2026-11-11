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

type dmiValue struct {
	Field string `json:"field"`
	Value string `json:"value"`
}

var dmiFields = []struct{ Label, File string }{
	{"System manufacturer", "sys_vendor"},
	{"System model", "product_name"},
	{"System version", "product_version"},
	{"Product family", "product_family"},
	{"Product SKU", "product_sku"},
	{"Motherboard manufacturer", "board_vendor"},
	{"Motherboard model", "board_name"},
	{"Motherboard version", "board_version"},
	{"Chassis manufacturer", "chassis_vendor"},
	{"Chassis type", "chassis_type"},
	{"Chassis version", "chassis_version"},
	{"BIOS vendor", "bios_vendor"},
	{"BIOS version", "bios_version"},
	{"BIOS date", "bios_date"},
}

var dmiSerialFields = []struct{ Label, File string }{
	{"System serial", "product_serial"},
	{"Motherboard serial", "board_serial"},
	{"Chassis serial", "chassis_serial"},
}

func dmiRead(base, file string) string {
	return read(filepath.Join(base, file))
}

// wslHostIdentity asks the Windows host for SMBIOS data, because WSL itself
// does not expose /sys/class/dmi. It is a best-effort fallback and stays
// silent when PowerShell interop is unavailable.
func wslHostIdentity(ctx context.Context, timeout time.Duration) []dmiValue {
	if !isWSL() || !system.Which("powershell.exe") {
		return nil
	}
	const script = `$bb = Get-CimInstance Win32_BaseBoard
$cs = Get-CimInstance Win32_ComputerSystem
$bios = Get-CimInstance Win32_BIOS
[pscustomobject]@{
  board_manufacturer = $bb.Manufacturer
  board_product = $bb.Product
  board_version = $bb.Version
  system_manufacturer = $cs.Manufacturer
  system_model = $cs.Model
  bios_vendor = $bios.Manufacturer
  bios_version = $bios.SMBIOSBIOSVersion
  bios_date = $(if ($bios.ReleaseDate) { $bios.ReleaseDate.ToString('yyyy-MM-dd') } else { '' })
} | ConvertTo-Json -Compress`
	out, _, code := command(ctx, timeout, "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script)
	if code != 0 {
		return nil
	}
	var raw map[string]any
	if json.Unmarshal([]byte(strings.TrimSpace(out)), &raw) != nil {
		return nil
	}
	fields := []struct{ Key, Label string }{
		{"board_manufacturer", "Motherboard manufacturer"},
		{"board_product", "Motherboard model"},
		{"board_version", "Motherboard version"},
		{"system_manufacturer", "System manufacturer"},
		{"system_model", "System model"},
		{"bios_vendor", "BIOS vendor"},
		{"bios_version", "BIOS version"},
		{"bios_date", "BIOS date"},
	}
	values := []dmiValue{}
	for _, f := range fields {
		if v, ok := raw[f.Key].(string); ok && strings.TrimSpace(v) != "" {
			values = append(values, dmiValue{f.Label, v})
		}
	}
	return values
}

func usbDevices() []map[string]string {
	out := []map[string]string{}
	for _, dir := range glob("/sys/bus/usb/devices/*") {
		vendor := read(dir + "/idVendor")
		product := read(dir + "/idProduct")
		name := read(dir + "/product")
		manufacturer := read(dir + "/manufacturer")
		if name == "" && manufacturer == "" {
			continue
		}
		if vendor == "1d6b" {
			continue // Linux Foundation root hubs
		}
		out = append(out, map[string]string{
			"name":         name,
			"manufacturer": manufacturer,
			"vendor_id":    vendor,
			"product_id":   product,
		})
		if len(out) >= 24 {
			break
		}
	}
	return out
}

func collectBoard(ctx context.Context, c string, timeout time.Duration, cfg config.Config) []Result {
	base := "/sys/class/dmi/id/"
	values := []dmiValue{}
	for _, f := range dmiFields {
		v := dmiRead(base, f.File)
		if v == "" {
			continue
		}
		values = append(values, dmiValue{f.Label, v})
	}
	if !cfg.Privacy {
		for _, f := range dmiSerialFields {
			if v := dmiRead(base, f.File); v != "" {
				values = append(values, dmiValue{f.Label, v})
			}
		}
	}

	results := []Result{}
	if len(values) > 0 {
		results = append(results, pass(c, "System and board identity", values, "Read directly from /sys/class/dmi/id."))
	} else if host := wslHostIdentity(ctx, timeout); len(host) > 0 {
		results = append(results, pass(c, "System and board identity", host, "WSL does not expose DMI/SMBIOS data, so these values were read from the Windows host through CIM."))
	} else if isWSL() {
		results = append(results, skip(c, "System and board identity", "WSL does not expose DMI/SMBIOS data, and the Windows host could not be queried."))
	} else if isContainer() {
		results = append(results, skip(c, "System and board identity", "A container does not expose the host's DMI/SMBIOS data."))
	} else {
		results = append(results, skip(c, "System and board identity", "DMI values are unavailable or access restricted. Try again with administrator privileges."))
	}
	if cfg.Privacy {
		results = append(results, skip(c, "Serial numbers", "Hidden in privacy mode."))
	}

	if usb := usbDevices(); len(usb) > 0 {
		results = append(results, pass(c, "USB devices", usb, "Enumerated from /sys/bus/usb/devices, no lsusb required."))
	}

	if count := len(glob("/sys/bus/pci/devices/*")); count > 0 {
		results = append(results, pass(c, "PCI devices", count, "Total PCI devices visible to the kernel."))
	}

	if system.Which("lsusb") {
		out, err, code := command(ctx, timeout, "lsusb")
		if code == 0 {
			results = append(results, pass(c, "USB topology (lsusb)", strings.TrimSpace(out), "Optional detail when usbutils is installed."))
		} else {
			results = append(results, skip(c, "USB topology (lsusb)", err))
		}
	}

	if !cfg.Privacy && !isWSL() && system.Which("dmidecode") {
		out, err, code := command(ctx, timeout, "dmidecode", "--type", "0,1,2")
		if code == 0 {
			results = append(results, pass(c, "Detailed firmware and board inventory", strings.TrimSpace(out), "Collected with dmidecode; administrator privileges may be required."))
		} else {
			results = append(results, skip(c, "Detailed firmware and board inventory", err+" (administrator privileges may be required)."))
		}
	}
	return results
}
