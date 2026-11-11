package diagnostics

import (
	"context"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/system"
)

func defaultRoutes() []map[string]string {
	out := []map[string]string{}
	for _, line := range strings.Split(read("/proc/net/route"), "\n") {
		f := strings.Fields(line)
		if len(f) < 8 || f[1] != "00000000" {
			continue
		}
		out = append(out, map[string]string{
			"interface": f[0],
			"gateway":   hexIPv4(f[2]),
			"metric":    f[6],
		})
	}
	return out
}

func hexIPv4(h string) string {
	n, err := strconv.ParseUint(h, 16, 32)
	if err != nil {
		return h
	}
	return fmt.Sprintf("%d.%d.%d.%d", byte(n), byte(n>>8), byte(n>>16), byte(n>>24))
}

func collectNetwork(ctx context.Context, c string, timeout time.Duration, cfg config.Config) []Result {
	interfaces := []map[string]any{}
	if addrs, err := net.Interfaces(); err == nil {
		for _, iface := range addrs {
			entry := map[string]any{
				"name":  iface.Name,
				"mtu":   iface.MTU,
				"flags": iface.Flags.String(),
				"up":    iface.Flags&net.FlagUp != 0,
			}
			sysDir := "/sys/class/net/" + iface.Name
			if state := read(sysDir + "/operstate"); state != "" {
				entry["state"] = state
			}
			if speed, ok := readInt(sysDir + "/speed"); ok && speed > 0 {
				entry["speed_mbps"] = speed
			}
			if duplex := read(sysDir + "/duplex"); duplex != "" {
				entry["duplex"] = duplex
			}
			if _, err := os.Stat(sysDir + "/wireless"); err == nil {
				entry["wireless"] = true
			}
			if cfg.Privacy {
				entry["mac"] = "[REDACTED]"
				entry["addresses"] = "[REDACTED]"
			} else {
				entry["mac"] = iface.HardwareAddr.String()
				list := []string{}
				if a, err := iface.Addrs(); err == nil {
					for _, addr := range a {
						list = append(list, addr.String())
					}
				}
				entry["addresses"] = list
			}
			interfaces = append(interfaces, entry)
		}
	}

	results := []Result{}
	if len(interfaces) > 0 {
		results = append(results, pass(c, "Network interfaces", interfaces, "Collected with the Go standard library plus /sys/class/net link details."))
	} else {
		results = append(results, skip(c, "Network interfaces", "No network interfaces were reported."))
	}

	if routes := defaultRoutes(); len(routes) > 0 {
		results = append(results, pass(c, "Default routes", routes, "Parsed from /proc/net/route."))
	} else {
		results = append(results, skip(c, "Default routes", "No IPv4 default route found."))
	}

	dns := []string{}
	for _, line := range strings.Split(read("/etc/resolv.conf"), "\n") {
		f := strings.Fields(line)
		if len(f) > 1 && f[0] == "nameserver" {
			if cfg.Privacy {
				dns = append(dns, "[REDACTED]")
			} else {
				dns = append(dns, f[1])
			}
		}
	}
	results = append(results, pass(c, "Configured DNS servers", dns, "Parsed from /etc/resolv.conf."))

	if system.Which("iwgetid") {
		if ssid, err, code := command(ctx, timeout, "iwgetid", "-r"); code == 0 {
			if cfg.Privacy {
				ssid = "[REDACTED]"
			}
			results = append(results, pass(c, "Wi-Fi network", strings.TrimSpace(ssid), "SSID reported by iwgetid."))
		} else if strings.TrimSpace(err) != "" {
			results = append(results, skip(c, "Wi-Fi network", err))
		}
	}

	if cfg.Network {
		start := time.Now()
		conn, err := net.DialTimeout("tcp", "1.1.1.1:53", 3*time.Second)
		if err == nil {
			_ = conn.Close()
			results = append(results, pass(c, "TCP connectivity test", fmt.Sprintf("Reachable in %d ms", time.Since(start).Milliseconds()), "Opt-in connection to 1.1.1.1:53."))
		} else {
			results = append(results, warn(c, "TCP connectivity test", "Unreachable", err.Error()))
		}
		start = time.Now()
		ips, err := net.LookupHost("example.com")
		if err == nil {
			value := ips
			if cfg.Privacy {
				value = []string{"[REDACTED]"}
			}
			results = append(results, pass(c, "DNS resolution test", map[string]any{
				"answers":     value,
				"duration_ms": time.Since(start).Milliseconds(),
			}, "System resolver lookup for example.com."))
		} else {
			results = append(results, warn(c, "DNS resolution test", "Failed", err.Error()))
		}
	} else {
		results = append(results, skip(c, "Connectivity tests", "Network tests are opt-in and disabled in settings."))
	}
	return results
}
