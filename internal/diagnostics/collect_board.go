package diagnostics

import (
	"context"
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
	} else {
		results = append(results, skip(c, "System and board identity", "DMI values are unavailable or access restricted."))
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

	if !cfg.Privacy && system.Which("dmidecode") {
		out, err, code := command(ctx, timeout, "dmidecode", "--type", "0,1,2")
		if code == 0 {
			results = append(results, pass(c, "Detailed firmware and board inventory", strings.TrimSpace(out), "Collected with dmidecode; administrator privileges may be required."))
		} else {
			results = append(results, skip(c, "Detailed firmware and board inventory", err+" (administrator privileges may be required)."))
		}
	}
	return results
}
