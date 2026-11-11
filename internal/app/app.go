package app

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/charmbracelet/bubbles/spinner"
	tea "github.com/charmbracelet/bubbletea"
	"github.com/charmbracelet/lipgloss"
	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/diagnostics"
	"github.com/tenkayro/syspect/internal/report"
	"github.com/tenkayro/syspect/internal/system"
	"github.com/tenkayro/syspect/internal/tools"
)

var title = lipgloss.NewStyle().Bold(true).Foreground(lipgloss.Color("#8BE9FD")).Padding(0, 1)
var muted = lipgloss.NewStyle().Foreground(lipgloss.Color("#7F8792"))
var selected = lipgloss.NewStyle().Foreground(lipgloss.Color("#50FA7B")).Bold(true)
var panel = lipgloss.NewStyle().Border(lipgloss.RoundedBorder()).BorderForeground(lipgloss.Color("#44475A")).Padding(1, 2)

type scanMsg struct {
	index    int
	category diagnostics.Category
	results  []diagnostics.Result
	err      error
	done     bool
	started  time.Time
}
type installMsg struct{ err error }
type model struct {
	cfg           config.Config
	menu          int
	profile       string
	state         string
	width, height int
	spinner       spinner.Model
	index         int
	selected      []diagnostics.Category
	run           diagnostics.Run
	started       time.Time
	scanContext   context.Context
	scanCancel    context.CancelFunc
	message       string
	toolIndex     int
	tools         []tools.Tool
	toolSelected  map[int]bool
	pendingTools  []tools.Tool
}

var menuItems = []string{"Run full diagnostics", "Run quick scan", "Hardware overview", "Server overview", "Install diagnostic tools", "Settings", "View report location", "Exit"}

func New() tea.Model {
	s := spinner.New()
	s.Spinner = spinner.Dot
	return model{cfg: config.Load(), state: "menu", spinner: s, profile: "full", tools: tools.RelevantCatalog(), toolSelected: map[int]bool{}}
}
func (m model) Init() tea.Cmd { return m.spinner.Tick }
func (m model) scanNext() tea.Cmd {
	if m.index >= len(m.selected) {
		return func() tea.Msg { return scanMsg{done: true, started: m.started} }
	}
	cat := m.selected[m.index]
	cfg := m.cfg
	ctx := m.scanContext
	index := m.index
	return func() tea.Msg {
		return scanMsg{index: index, category: cat, results: diagnostics.Collect(ctx, cfg, cat)}
	}
}
func profileIDs(p string) []string {
	switch p {
	case "quick":
		return []string{"os", "cpu", "memory", "gpu", "storage", "virtualization"}
	case "hardware":
		return []string{"cpu", "memory", "gpu", "board", "storage", "network", "sensors", "battery"}
	case "server":
		return []string{"os", "cpu", "memory", "storage", "network", "kernel", "services", "logs", "virtualization"}
	default:
		return []string{"os", "cpu", "memory", "gpu", "board", "storage", "network", "sensors", "battery", "kernel", "services", "logs", "virtualization"}
	}
}
func (m model) startScan(profile string) (model, tea.Cmd) {
	m.profile = profile
	m.selected = nil
	ids := profileIDs(profile)
	for _, c := range diagnostics.Categories {
		for _, id := range ids {
			if c.ID == id {
				m.selected = append(m.selected, c)
			}
		}
	}
	m.index = 0
	m.run = diagnostics.Run{Started: time.Now().UTC().Format(time.RFC3339), Profile: profile}
	m.started = time.Now()
	m.scanContext, m.scanCancel = context.WithCancel(context.Background())
	m.state = "scanning"
	m.message = "Preparing diagnostic checks"
	return m, m.scanNext()
}
func (m model) Update(msg tea.Msg) (tea.Model, tea.Cmd) {
	switch msg := msg.(type) {
	case tea.WindowSizeMsg:
		m.width = msg.Width
		m.height = msg.Height
	case spinner.TickMsg:
		var cmd tea.Cmd
		m.spinner, cmd = m.spinner.Update(msg)
		return m, cmd
	case scanMsg:
		if m.state != "scanning" {
			return m, nil
		}
		if msg.done {
			m.run.Duration = time.Since(m.started).Seconds()
			m.run.Finalize()
			m.state = "results"
			hp, jp, e := report.Save(m.run, m.cfg)
			if e != nil {
				m.message = "Report export failed: " + e.Error()
			} else {
				m.message = "Reports saved"
				if hp != "" {
					m.message += " · HTML: " + hp
				}
				if jp != "" {
					m.message += " · JSON: " + jp
				}
			}
			return m, nil
		}
		if msg.index == m.index {
			m.run.Results = append(m.run.Results, msg.results...)
			m.run.Completed = append(m.run.Completed, msg.category.ID)
			m.index++
			m.message = "Completed " + msg.category.Name
			if m.index < len(m.selected) {
				return m, m.scanNext()
			}
			return m, func() tea.Msg { return scanMsg{done: true} }
		}
	case installMsg:
		m.state = "tools"
		if msg.err != nil {
			m.message = msg.err.Error()
		} else {
			m.message = "Selected tools installed successfully."
		}
		return m, nil
	case tea.KeyMsg:
		if msg.String() == "ctrl+c" {
			return m, tea.Quit
		}
		switch m.state {
		case "menu":
			switch msg.String() {
			case "up", "k":
				if m.menu > 0 {
					m.menu--
				}
			case "down", "j":
				if m.menu < len(menuItems)-1 {
					m.menu++
				}
			case "enter", " ":
				switch m.menu {
				case 0:
					return m.startScan("full")
				case 1:
					return m.startScan("quick")
				case 2:
					return m.startScan("hardware")
				case 3:
					return m.startScan("server")
				case 4:
					m.state = "tools"
					m.message = "Select optional utilities to install. Existing tools are already checked."
				case 5:
					m.state = "settings"
				case 6:
					m.message = "Reports: " + system.DataDir() + "/reports"
				case 7:
					return m, tea.Quit
				}
			}
		case "scanning":
			if msg.String() == "esc" {
				m.run.Interrupted = true
				if m.scanCancel != nil {
					m.scanCancel()
				}
				m.run.Duration = time.Since(m.started).Seconds()
				m.run.Finalize()
				m.state = "results"
				m.message = "Scan interrupted. Completed results were preserved."
				hp, jp, _ := report.Save(m.run, m.cfg)
				if hp != "" {
					m.message += " · HTML report: " + hp
				}
				if jp != "" {
					m.message += " · JSON report: " + jp
				}
				return m, nil
			}
		case "results":
			if msg.String() == "enter" || msg.String() == "esc" || msg.String() == "q" {
				m.state = "menu"
				m.menu = 0
			}
		case "settings":
			switch msg.String() {
			case "esc", "q":
				m.state = "menu"
			case "1":
				m.cfg.Privacy = !m.cfg.Privacy
			case "2":
				m.cfg.HTML = !m.cfg.HTML
			case "3":
				m.cfg.JSON = !m.cfg.JSON
			case "4":
				m.cfg.Smart = !m.cfg.Smart
			case "5":
				m.cfg.Network = !m.cfg.Network
			case "6":
				m.cfg.Logs = !m.cfg.Logs
			case "s", "enter":
				if e := config.Save(m.cfg); e != nil {
					m.message = e.Error()
				} else {
					m.message = "Settings saved."
				}
			}
		case "confirm_install":
			switch msg.String() {
			case "y", "Y", "enter":
				m.state = "installing"
				chosen := append([]tools.Tool(nil), m.pendingTools...)
				return m, func() tea.Msg { return installMsg{err: tools.Install(chosen)} }
			case "n", "N", "esc":
				m.state = "tools"
			}
		case "tools":
			switch msg.String() {
			case "esc", "q":
				m.state = "menu"
			case "up", "k":
				if m.toolIndex > 0 {
					m.toolIndex--
				}
			case "down", "j":
				if m.toolIndex < len(m.tools)-1 {
					m.toolIndex++
				}
			case " ":
				t := m.tools[m.toolIndex]
				if !system.Which(t.Command) {
					m.toolSelected[m.toolIndex] = !m.toolSelected[m.toolIndex]
				}
			case "a":
				for i, t := range m.tools {
					if !system.Which(t.Command) {
						m.toolSelected[i] = true
					}
				}
			case "enter":
				chosen := []tools.Tool{}
				for i, t := range m.tools {
					if m.toolSelected[i] && !system.Which(t.Command) {
						chosen = append(chosen, t)
					}
				}
				if len(chosen) == 0 {
					m.message = "No missing tools selected."
					return m, nil
				}
				m.pendingTools = chosen
				m.state = "confirm_install"
				m.message = "Install " + fmt.Sprint(len(chosen)) + " selected package(s)? This will use your system package manager and may request sudo."
			}
		}
	}
	return m, nil
}
func (m model) View() string {
	if m.width == 0 {
		return "Loading Syspect..."
	}
	header := title.Render("SYSPECT") + muted.Render("  Linux System Diagnostics · v2.0.0") + "\n" + muted.Render(fmt.Sprintf("%s  ·  %s  ·  %s", system.Distribution().Name, system.Architecture(), system.Init()))
	var body string
	switch m.state {
	case "menu":
		rows := []string{}
		for i, x := range menuItems {
			p := "  "
			if i == m.menu {
				p = selected.Render("› ")
			}
			rows = append(rows, p+x)
		}
		body = panel.Render("Welcome to Syspect\n\nInspect your Linux system, review hardware and software details, and create local diagnostic reports.\n\n"+strings.Join(rows, "\n")) + "\n\n" + muted.Render("↑/↓ Navigate  ·  Enter Select  ·  Ctrl+C Quit")
	case "scanning":
		current := "Preparing..."
		if m.index < len(m.selected) {
			current = m.selected[m.index].Name
		}
		body = panel.Render(m.spinner.View() + " Running diagnostics\n\n" + current + "\n\n" + fmt.Sprintf("Category %d of %d", m.index+1, len(m.selected)) + "\n" + m.message + "\n\n" + muted.Render("Esc Interrupt scan"))
	case "results":
		s := m.run.Summary
		body = panel.Render("Diagnostic results\n\n" + fmt.Sprintf("Overall: %v\nTotal checks: %v\nPASS: %v   WARNING: %v   FAIL: %v   SKIPPED: %v", s["overall"], s["total"], s["pass"], s["warning"], s["fail"], s["skipped"]) + "\n\n" + m.message + "\n\n" + muted.Render("Enter Return to main menu"))
	case "settings":
		body = panel.Render("Settings\n\n" + fmt.Sprintf("1  Privacy mode: %t\n2  HTML reports: %t\n3  JSON reports: %t\n4  SMART checks: %t\n5  Network connectivity test: %t\n6  System log inspection: %t", m.cfg.Privacy, m.cfg.HTML, m.cfg.JSON, m.cfg.Smart, m.cfg.Network, m.cfg.Logs) + "\n\nPress number to toggle · S to save · Esc to return")
	case "tools":
		rows := []string{}
		for i, t := range m.tools {
			mark := "[ ]"
			status := "Missing"
			if system.Which(t.Command) {
				mark = "[x]"
				status = "Installed"
			}
			if m.toolSelected[i] {
				mark = "[x]"
			}
			prefix := "  "
			if i == m.toolIndex {
				prefix = selected.Render("› ")
			}
			rows = append(rows, prefix+fmt.Sprintf("%-4s %-12s %-10s %s", mark, t.Command, status, t.Purpose))
		}
		body = panel.Render("Optional diagnostic utilities\n\n" + strings.Join(rows, "\n") + "\n\n" + m.message + "\n\n↑/↓ Navigate · Space Toggle · A Select missing · Enter Install · Esc Back")
	case "confirm_install":
		lines := []string{}
		for _, t := range m.pendingTools {
			lines = append(lines, "• "+t.Command+" — "+t.Purpose)
		}
		body = panel.Render("Confirm package installation\n\n" + strings.Join(lines, "\n") + "\n\n" + m.message + "\n\nY / Enter: Install · N / Esc: Cancel")
	case "installing":
		body = panel.Render(m.spinner.View() + " Installing selected packages...\n\nThe package manager may request your administrator password.\n" + m.message)
	}
	return lipgloss.NewStyle().Padding(1, 2).Render(header + "\n\n" + body)
}
