package config

import "testing"

func TestDefaultConfig(t *testing.T) {
	c := Default()
	if c.Profile != "full" || !c.HTML || !c.JSON || c.Timeout < 2 {
		t.Fatalf("unexpected defaults: %#v", c)
	}
}
