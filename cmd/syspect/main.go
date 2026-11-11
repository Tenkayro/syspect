package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	tea "github.com/charmbracelet/bubbletea"
	"github.com/tenkayro/syspect/internal/app"
	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/diagnostics"
	"github.com/tenkayro/syspect/internal/system"
)

const version = "2.0.0"

const helpText = `Syspect — Linux System Diagnostics

Usage:
  syspect                       Open the interactive terminal interface
  syspect --scan [profile]      Run a scan and print JSON to stdout
  syspect --network             Enable opt-in network tests (use with --scan)
  syspect --paths               Print the data directory
  syspect --version             Print the version
  syspect --help                Show this help

Profiles for --scan:
  full       Everything except the DNS benchmark (default)
  quick      Fast overview
  hardware   CPU, memory, GPU, board, storage, network, sensors, battery
  server     OS, CPU, memory, storage, network, kernel, services, logs, security
  dns        DNS resolver benchmark (implies --network)

The DNS benchmark sends queries to public resolvers and never changes
your system DNS configuration.`

func main() {
	args := os.Args[1:]
	network := false
	filtered := make([]string, 0, len(args))
	for _, a := range args {
		if a == "--network" {
			network = true
			continue
		}
		filtered = append(filtered, a)
	}
	args = filtered

	if len(args) > 0 {
		switch args[0] {
		case "--version", "-v":
			fmt.Println("Syspect", version)
			return
		case "--help", "-h":
			fmt.Println(helpText)
			return
		case "--paths":
			fmt.Println(system.DataDir())
			return
		case "--scan":
			profile := ""
			if len(args) > 1 {
				profile = args[1]
			}
			runScan(profile, network)
			return
		default:
			fmt.Fprintf(os.Stderr, "Unknown option: %s\n\n%s\n", args[0], helpText)
			os.Exit(1)
		}
	}

	p := tea.NewProgram(app.New(), tea.WithAltScreen())
	if _, e := p.Run(); e != nil {
		fmt.Fprintln(os.Stderr, "Syspect could not start:", e)
		os.Exit(1)
	}
}

func runScan(profile string, network bool) {
	cfg := config.Load()
	if profile == "" {
		profile = cfg.Profile
		if profile == "" {
			profile = "full"
		}
	}
	if network || profile == "dns" {
		cfg.Network = true
	}
	cats := diagnostics.ProfileCategories(profile)
	if len(cats) == 0 {
		fmt.Fprintf(os.Stderr, "Unknown profile %q.\n\nAvailable: full, quick, hardware, server, dns\n", profile)
		os.Exit(2)
	}

	run := diagnostics.Run{
		Started: time.Now().UTC().Format(time.RFC3339),
		Profile: profile,
	}
	ctx := context.Background()
	started := time.Now()
	for i, cat := range cats {
		fmt.Fprintf(os.Stderr, "[%d/%d] %s\n", i+1, len(cats), cat.Name)
		run.Results = append(run.Results, diagnostics.Collect(ctx, cfg, cat)...)
		run.Completed = append(run.Completed, cat.ID)
	}
	run.Duration = time.Since(started).Seconds()
	run.Finalize()

	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", "  ")
	if err := enc.Encode(run); err != nil {
		fmt.Fprintln(os.Stderr, "Could not encode result:", err)
		os.Exit(1)
	}
}
