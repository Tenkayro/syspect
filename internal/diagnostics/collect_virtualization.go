package diagnostics

import (
	"context"
	"os"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/system"
)

func collectVirtualization(ctx context.Context, c string, timeout time.Duration, cfg config.Config) []Result {
	results := []Result{}

	vm := detectVM()
	if len(vm) > 0 {
		results = append(results, pass(c, "Virtual machine", vm, "Detection from DMI, /sys/hypervisor and CPU flags."))
	} else {
		results = append(results, pass(c, "Virtual machine", "none detected", ""))
	}

	container := detectContainer()
	if len(container) > 0 {
		results = append(results, pass(c, "Container environment", container, "Detection from container marker files and cgroups."))
	} else {
		results = append(results, pass(c, "Container environment", "none detected", ""))
	}

	if hypervisor := read("/sys/hypervisor/type"); hypervisor != "" {
		results = append(results, pass(c, "Hypervisor", hypervisor, ""))
	}

	if system.Which("systemd-detect-virt") {
		if o, _, _ := command(ctx, timeout, "systemd-detect-virt", "--vm"); strings.TrimSpace(o) != "" {
			results = append(results, pass(c, "systemd-detect-virt (VM)", strings.TrimSpace(o), ""))
		}
		if o, _, _ := command(ctx, timeout, "systemd-detect-virt", "--container"); strings.TrimSpace(o) != "" {
			results = append(results, pass(c, "systemd-detect-virt (container)", strings.TrimSpace(o), ""))
		}
	}

	found := []string{}
	for _, tool := range []string{"docker", "podman", "lxc", "virsh", "wsl"} {
		if system.Which(tool) {
			found = append(found, tool)
		}
	}
	results = append(results, pass(c, "Container and VM management tools", found, ""))
	return results
}

func detectVM() []string {
	out := []string{}
	if isWSL() {
		out = append(out, "WSL2 (Hyper-V)")
	}
	haystack := strings.ToLower(read("/sys/class/dmi/id/product_name") + " " + read("/sys/class/dmi/id/sys_vendor") + " " + read("/sys/class/dmi/id/board_vendor"))
	matches := []struct{ needle, name string }{
		{"vmware", "VMware"},
		{"virtualbox", "VirtualBox"},
		{"innotek", "VirtualBox"},
		{"kvm", "KVM"},
		{"qemu", "QEMU"},
		{"bochs", "Bochs"},
		{"hyper-v", "Hyper-V"},
		{"microsoft corporation virtual machine", "Hyper-V"},
		{"xen", "Xen"},
		{"parallels", "Parallels"},
		{"bhyve", "bhyve"},
		{"amazon ec2", "Amazon EC2"},
		{"google compute", "Google Compute Engine"},
		{"openstack", "OpenStack"},
	}
	for _, m := range matches {
		if strings.Contains(haystack, m.needle) {
			out = append(out, m.name)
		}
	}
	if hypervisor := read("/sys/hypervisor/type"); hypervisor != "" {
		out = append(out, hypervisor)
	}
	if len(out) == 0 && (strings.Contains(strings.ToLower(read("/proc/cpuinfo")), " hypervisor") || strings.Contains(strings.ToLower(read("/proc/cpuinfo")), "\nhypervisor")) {
		out = append(out, "Hypervisor (CPU flag)")
	}
	return uniqueStrings(out)
}

func detectContainer() []string {
	out := []string{}
	if _, err := os.Stat("/.dockerenv"); err == nil {
		out = append(out, "Docker")
	}
	if _, err := os.Stat("/run/.containerenv"); err == nil {
		out = append(out, "Podman/container")
	}
	cgroup := strings.ToLower(read("/proc/1/cgroup") + read("/proc/self/cgroup"))
	for _, marker := range []string{"docker", "kubepods", "containerd", "lxc", "podman", "libpod"} {
		if strings.Contains(cgroup, marker) {
			out = append(out, marker)
		}
	}
	version := strings.ToLower(read("/proc/version"))
	if strings.Contains(version, "microsoft") || strings.Contains(version, "wsl") {
		out = append(out, "WSL")
	}
	return uniqueStrings(out)
}

func isWSL() bool {
	version := strings.ToLower(read("/proc/version") + read("/proc/sys/kernel/osrelease"))
	return strings.Contains(version, "microsoft") || strings.Contains(version, "wsl")
}

func isContainer() bool {
	if _, err := os.Stat("/.dockerenv"); err == nil {
		return true
	}
	if _, err := os.Stat("/run/.containerenv"); err == nil {
		return true
	}
	cgroup := strings.ToLower(read("/proc/1/cgroup"))
	for _, marker := range []string{"docker", "kubepods", "containerd", "lxc", "libpod"} {
		if strings.Contains(cgroup, marker) {
			return true
		}
	}
	return false
}

func uniqueStrings(in []string) []string {
	seen := map[string]bool{}
	out := []string{}
	for _, x := range in {
		if x == "" || seen[x] {
			continue
		}
		seen[x] = true
		out = append(out, x)
	}
	return out
}
