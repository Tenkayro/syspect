package tools

import (
	"fmt"
	"github.com/tenkayro/syspect/internal/system"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

type Tool struct{ Command, Package, Purpose string }

var Catalog = []Tool{{"lspci", "pciutils", "PCI and graphics hardware details"}, {"lsusb", "usbutils", "USB device inventory"}, {"dmidecode", "dmidecode", "Firmware and board details"}, {"smartctl", "smartmontools", "Drive SMART health"}, {"nvme", "nvme-cli", "NVMe health details"}, {"sensors", "lm-sensors", "Temperatures and fan sensors"}, {"lsblk", "util-linux", "Block device inventory"}, {"df", "coreutils", "Filesystem usage"}, {"ps", "procps", "Process information"}, {"ip", "iproute2", "Network interfaces and routes"}, {"systemctl", "systemd", "Service status"}, {"journalctl", "systemd", "System journal diagnostics"}}

func RelevantCatalog() []Tool {
	relevant := []Tool{}
	has := func(pattern string) bool { matches, _ := filepath.Glob(pattern); return len(matches) > 0 }
	for _, t := range Catalog {
		include := true
		switch t.Command {
		case "lspci":
			include = has("/sys/bus/pci/devices/*")
		case "lsusb":
			include = has("/sys/bus/usb/devices/*")
		case "dmidecode":
			include = has("/sys/class/dmi/id/*")
		case "smartctl":
			include = has("/sys/block/*")
		case "nvme":
			include = has("/sys/class/nvme/*") || has("/dev/nvme*")
		case "sensors":
			include = has("/sys/class/hwmon/*")
		case "ethtool":
			include = has("/sys/class/net/*")
		case "systemctl", "journalctl":
			include = system.Init() == "systemd"
		}
		if include {
			relevant = append(relevant, t)
		}
	}
	return relevant
}

func Package(t Tool, distro string) string {
	if t.Command == "sensors" && (distro == "fedora" || distro == "rhel" || distro == "arch" || distro == "manjaro" || distro == "alpine") {
		return "lm_sensors"
	}
	return t.Package
}
func Install(selected []Tool) error {
	manager := system.PackageManager()
	if manager == "" {
		return fmt.Errorf("No supported package manager detected")
	}
	failures := []string{}
	seen := map[string]bool{}
	for _, t := range selected {
		if system.Which(t.Command) {
			continue
		}
		pkg := Package(t, system.Distribution().ID)
		if seen[pkg] {
			continue
		}
		seen[pkg] = true
		var args []string
		switch manager {
		case "apt-get", "apt", "dnf", "yum":
			args = []string{"install", "-y", pkg}
		case "pacman":
			args = []string{"-S", "--needed", "--noconfirm", pkg}
		case "zypper":
			args = []string{"--non-interactive", "install", pkg}
		case "apk":
			args = []string{"add", pkg}
		case "xbps-install":
			args = []string{"-y", pkg}
		case "emerge":
			args = []string{"--ask=n", pkg}
		default:
			return fmt.Errorf("Unsupported package manager: %s", manager)
		}
		executable := manager
		if os.Geteuid() != 0 {
			if _, err := exec.LookPath("sudo"); err != nil {
				failures = append(failures, pkg+": sudo is unavailable")
				continue
			}
			args = append([]string{manager}, args...)
			executable = "sudo"
		}
		cmd := exec.Command(executable, args...)
		cmd.Stdin, cmd.Stdout, cmd.Stderr = os.Stdin, os.Stdout, os.Stderr
		if err := cmd.Run(); err != nil {
			failures = append(failures, pkg+": "+err.Error())
			continue
		}
	}
	for _, t := range selected {
		if !system.Which(t.Command) {
			failures = append(failures, t.Package+": command "+t.Command+" is still unavailable")
		}
	}
	if len(failures) > 0 {
		return fmt.Errorf("Some tools could not be installed:\n%s", strings.Join(failures, "\n"))
	}
	return nil
}
