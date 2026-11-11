package diagnostics

import (
	"context"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/system"
)

const secureBootVar = "/sys/firmware/efi/efivars/SecureBoot-8be4df61-93ca-11d2-aa0d-00e098032b8c"

func secureBootState() (bool, bool) {
	data, err := os.ReadFile(secureBootVar)
	if err != nil || len(data) < 5 {
		return false, false
	}
	return data[4] == 1, true
}

func collectSecurity(ctx context.Context, c string, timeout time.Duration) []Result {
	results := []Result{}

	sbValue := "Unavailable (legacy boot or efivarfs not mounted)"
	if sb, ok := secureBootState(); ok {
		if sb {
			sbValue = "Enabled"
		} else {
			sbValue = "Disabled"
		}
	}
	results = append(results, pass(c, "Secure Boot", sbValue, "Read from the EFI SecureBoot variable."))

	if tpms := glob("/sys/class/tpm/tpm*"); len(tpms) > 0 {
		entry := map[string]any{"devices": len(tpms)}
		for _, dir := range tpms {
			if v := read(dir + "/tpm_version_major"); v != "" {
				entry["version"] = v
				break
			}
		}
		results = append(results, pass(c, "TPM", entry, ""))
	} else {
		results = append(results, skip(c, "TPM", "No TPM device exposed by the kernel."))
	}

	if lsm := read("/sys/kernel/security/lsm"); lsm != "" {
		results = append(results, pass(c, "Active Linux Security Modules", lsm, ""))
	}

	results = append(results, pass(c, "SELinux", selinuxState(), ""))
	results = append(results, pass(c, "AppArmor", apparmorState(), ""))

	firewall := map[string]string{}
	for _, unit := range []string{"ufw", "firewalld", "nftables"} {
		if !system.Which("systemctl") {
			continue
		}
		if _, _, code := command(ctx, timeout, "systemctl", "is-active", "--quiet", unit); code == 0 {
			firewall[unit] = "active"
		} else {
			firewall[unit] = "inactive"
		}
	}
	tools := []string{}
	for _, tool := range []string{"ufw", "firewall-cmd", "nft", "iptables"} {
		if system.Which(tool) {
			tools = append(tools, tool)
		}
	}
	results = append(results, pass(c, "Firewall", map[string]any{
		"services": firewall,
		"tools":    tools,
	}, "Service state plus installed firewall tools. Rules are not enumerated without root."))

	sysctls := map[string]string{}
	for _, name := range []string{"randomize_va_space", "dmesg_restrict", "kptr_restrict", "unprivileged_bpf_disabled", "perf_event_paranoid"} {
		if v := read("/proc/sys/kernel/" + name); v != "" {
			sysctls[name] = v
		}
	}
	status := "PASS"
	severity := "info"
	if aslr, err := strconv.Atoi(sysctls["randomize_va_space"]); err == nil && aslr == 0 {
		status = "WARNING"
		severity = "warning"
	}
	results = append(results, Result{c, "Kernel hardening settings", status, sysctls, "ASLR and kernel information exposure settings.", "", severity})

	if lockdown := read("/sys/kernel/security/lockdown"); lockdown != "" {
		results = append(results, pass(c, "Kernel lockdown", lockdown, ""))
	}
	return results
}

func selinuxState() string {
	enforce, err := os.ReadFile("/sys/fs/selinux/enforce")
	if err == nil {
		switch strings.TrimSpace(string(enforce)) {
		case "1":
			return "Enforcing"
		case "0":
			return "Permissive"
		}
	}
	if _, err := os.Stat("/etc/selinux/config"); err == nil {
		return "Installed, not active"
	}
	return "Not present"
}

func apparmorState() string {
	if _, err := os.ReadDir("/sys/kernel/security/apparmor"); err != nil {
		if _, statErr := os.Stat("/etc/apparmor.d"); statErr == nil {
			return "Installed, not active"
		}
		return "Not present"
	}
	state := "Active"
	if profiles := read("/sys/kernel/security/apparmor/profiles"); profiles != "" {
		state = "Active (" + strconv.Itoa(len(nonEmptyLines(profiles))) + " profiles)"
	}
	return state
}
