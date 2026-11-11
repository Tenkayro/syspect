package diagnostics

import (
	"context"
	"testing"

	"github.com/tenkayro/syspect/internal/config"
)

func TestCollectMemoryReturnsStructuredResults(t *testing.T) {
	got := Collect(context.Background(), config.Default(), Category{ID: "memory", Name: "Memory"})
	if len(got) == 0 {
		t.Fatal("expected at least one memory result")
	}
	if got[0].Category != "Memory" {
		t.Fatalf("unexpected category: %q", got[0].Category)
	}
	if got[0].Status != "PASS" && got[0].Status != "SKIPPED" {
		t.Fatalf("unexpected status: %q", got[0].Status)
	}
}

func TestFinalizeCountsResults(t *testing.T) {
	r := Run{Results: []Result{{Status: "PASS"}, {Status: "WARNING"}, {Status: "SKIPPED"}}}
	r.Finalize()
	if r.Summary["overall"] != "WARNING" || r.Summary["total"] != 3 {
		t.Fatalf("unexpected summary: %#v", r.Summary)
	}
}
