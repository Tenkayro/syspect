package config

import (
	"encoding/json"
	"os"
	"path/filepath"
)

type Config struct {
	Profile string `json:"default_profile"`
	Privacy bool   `json:"privacy_mode"`
	HTML    bool   `json:"html_report"`
	JSON    bool   `json:"json_report"`
	Smart   bool   `json:"smart_checks"`
	Network bool   `json:"network_connectivity"`
	Logs    bool   `json:"log_inspection"`
	Timeout int    `json:"command_timeout_seconds"`
}

func Default() Config { return Config{Profile: "full", HTML: true, JSON: true, Timeout: 12} }
func Path() string {
	if x := os.Getenv("SYSPECT_HOME"); x != "" {
		return filepath.Join(x, "config", "config.json")
	}
	root := os.Getenv("XDG_CONFIG_HOME")
	if root == "" {
		h, _ := os.UserHomeDir()
		root = filepath.Join(h, ".config")
	}
	return filepath.Join(root, "syspect", "config.json")
}
func Load() Config {
	c := Default()
	b, e := os.ReadFile(Path())
	if e == nil {
		_ = json.Unmarshal(b, &c)
	}
	if c.Timeout < 2 || c.Timeout > 120 {
		c.Timeout = 12
	}
	return c
}
func Save(c Config) error {
	p := Path()
	if e := os.MkdirAll(filepath.Dir(p), 0700); e != nil {
		return e
	}
	b, e := json.MarshalIndent(c, "", "  ")
	if e != nil {
		return e
	}
	return os.WriteFile(p, b, 0600)
}
