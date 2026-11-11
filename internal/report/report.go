package report

import (
	"encoding/json"
	"fmt"
	"github.com/tenkayro/syspect/internal/config"
	"github.com/tenkayro/syspect/internal/diagnostics"
	"github.com/tenkayro/syspect/internal/system"
	"html"
	"os"
	"path/filepath"
	"strings"
	"time"
)

func Save(run diagnostics.Run, cfg config.Config) (string, string, error) {
	root := system.DataDir()
	jdir := filepath.Join(root, "reports", "json")
	hdir := filepath.Join(root, "reports", "html")
	if e := os.MkdirAll(jdir, 0700); e != nil {
		return "", "", e
	}
	if e := os.MkdirAll(hdir, 0700); e != nil {
		return "", "", e
	}
	stamp := time.Now().Format("20060102-150405")
	slug := strings.ToLower(run.Profile)
	data := map[string]any{"schema_version": 1, "application": "Syspect", "version": "2.0.0", "generated_at": time.Now().UTC().Format(time.RFC3339), "scan_started_at": run.Started, "duration_seconds": run.Duration, "profile": run.Profile, "interrupted": run.Interrupted, "privacy_mode": cfg.Privacy, "summary": run.Summary, "completed_categories": run.Completed, "results": run.Results}
	var jp, hp string
	if cfg.JSON {
		jp = filepath.Join(jdir, fmt.Sprintf("syspect-%s-%s.json", slug, stamp))
		b, e := json.MarshalIndent(data, "", "  ")
		if e != nil {
			return "", "", e
		}
		if e = os.WriteFile(jp, append(b, '\n'), 0600); e != nil {
			return "", "", e
		}
	}
	if cfg.HTML {
		hp = filepath.Join(hdir, fmt.Sprintf("syspect-%s-%s.html", slug, stamp))
		if e := os.WriteFile(hp, []byte(render(data, run)), 0600); e != nil {
			return "", "", e
		}
	}
	return hp, jp, nil
}
func render(data map[string]any, run diagnostics.Run) string {
	var b strings.Builder
	b.WriteString(`<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Syspect Diagnostic Report</title><style>body{font:15px system-ui,sans-serif;max-width:1100px;margin:40px auto;padding:0 20px;color:#20242b}h1{font-size:2rem}table{width:100%;border-collapse:collapse;margin:14px 0 28px}th,td{text-align:left;padding:10px;border-bottom:1px solid #ddd;vertical-align:top}th{background:#f4f6f8}.status{font-weight:700}.PASS{color:#167344}.WARNING{color:#9a6700}.FAIL{color:#c22}.SKIPPED{color:#68717d}pre{white-space:pre-wrap;overflow-wrap:anywhere;margin:0}</style></head><body><h1>Syspect Diagnostic Report</h1><p>Generated locally · `)
	b.WriteString(html.EscapeString(time.Now().Format(time.RFC1123)))
	b.WriteString(`</p><h2>Summary</h2><pre>`)
	s, _ := json.MarshalIndent(run.Summary, "", "  ")
	b.WriteString(html.EscapeString(string(s)))
	b.WriteString(`</pre>`)
	groups := map[string][]diagnostics.Result{}
	order := []string{}
	for _, r := range run.Results {
		if _, ok := groups[r.Category]; !ok {
			order = append(order, r.Category)
		}
		groups[r.Category] = append(groups[r.Category], r)
	}
	for _, cat := range order {
		b.WriteString("<h2>" + html.EscapeString(cat) + "</h2><table><thead><tr><th>Check</th><th>Status</th><th>Value</th><th>Explanation</th></tr></thead><tbody>")
		for _, r := range groups[cat] {
			v, _ := json.MarshalIndent(r.Value, "", "  ")
			b.WriteString("<tr><td>" + html.EscapeString(r.Name) + "</td><td class=\"status " + r.Status + "\">" + r.Status + "</td><td><pre>" + html.EscapeString(string(v)) + "</pre></td><td>" + html.EscapeString(r.Explanation) + "</td></tr>")
		}
		b.WriteString("</tbody></table>")
	}
	b.WriteString("</body></html>")
	return b.String()
}
