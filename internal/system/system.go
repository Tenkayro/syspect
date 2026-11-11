package system

import (
	"bufio"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
)

type Distro struct{ ID, Like, Name, Version, Codename string }

func Distribution() Distro {
	d := Distro{ID: "unknown", Name: "Unknown Linux"}
	for _, p := range []string{"/etc/os-release", "/usr/lib/os-release"} {
		f, e := os.Open(p)
		if e != nil {
			continue
		}
		s := bufio.NewScanner(f)
		vals := map[string]string{}
		for s.Scan() {
			line := s.Text()
			if i := strings.IndexByte(line, '='); i > 0 {
				v := strings.Trim(strings.TrimSpace(line[i+1:]), "\"'")
				vals[line[:i]] = v
			}
		}
		f.Close()
		if len(vals) > 0 {
			d.ID = strings.ToLower(vals["ID"])
			d.Like = strings.ToLower(vals["ID_LIKE"])
			d.Name = vals["PRETTY_NAME"]
			if d.Name == "" {
				d.Name = vals["NAME"]
			}
			d.Version = vals["VERSION_ID"]
			d.Codename = vals["VERSION_CODENAME"]
			return d
		}
	}
	return d
}
func Init() string {
	if _, e := os.Stat("/run/systemd/system"); e == nil {
		return "systemd"
	}
	b, _ := os.ReadFile("/proc/1/comm")
	if len(b) > 0 {
		return strings.TrimSpace(string(b))
	}
	return "unknown"
}
func Which(s string) bool { _, e := exec.LookPath(s); return e == nil }
func PackageManager() string {
	d := Distribution()
	ids := strings.Fields(d.ID + " " + d.Like)
	m := map[string]string{"debian": "apt-get", "ubuntu": "apt-get", "linuxmint": "apt-get", "pop": "apt-get", "fedora": "dnf", "rhel": "dnf", "centos": "dnf", "rocky": "dnf", "almalinux": "dnf", "arch": "pacman", "manjaro": "pacman", "opensuse": "zypper", "opensuse-leap": "zypper", "opensuse-tumbleweed": "zypper", "alpine": "apk", "void": "xbps-install", "gentoo": "emerge"}
	for _, id := range ids {
		if c := m[id]; c != "" && Which(c) {
			return c
		}
	}
	for _, c := range []string{"apt-get", "dnf", "yum", "pacman", "zypper", "apk", "xbps-install", "emerge"} {
		if Which(c) {
			return c
		}
	}
	return ""
}
func DataDir() string {
	if x := os.Getenv("SYSPECT_HOME"); x != "" {
		return x
	}
	x := os.Getenv("XDG_DATA_HOME")
	if x == "" {
		h, _ := os.UserHomeDir()
		x = filepath.Join(h, ".local", "share")
	}
	return filepath.Join(x, "syspect")
}
func Architecture() string { return runtime.GOARCH }
