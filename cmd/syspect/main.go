package main

import (
	"fmt"
	tea "github.com/charmbracelet/bubbletea"
	"github.com/tenkayro/syspect/internal/app"
	"github.com/tenkayro/syspect/internal/system"
	"os"
)

const version = "2.0.0"

func main() {
	if len(os.Args) > 1 {
		switch os.Args[1] {
		case "--version", "-v":
			fmt.Println("Syspect", version)
			return
		case "--help", "-h":
			fmt.Println("Syspect — Linux System Diagnostics\n\nUsage: syspect [--version|--help]\nRun without arguments to open the interactive terminal interface.")
			return
		case "--paths":
			fmt.Println(system.DataDir())
			return
		}
	}
	p := tea.NewProgram(app.New(), tea.WithAltScreen())
	if _, e := p.Run(); e != nil {
		fmt.Fprintln(os.Stderr, "Syspect could not start:", e)
		os.Exit(1)
	}
}
