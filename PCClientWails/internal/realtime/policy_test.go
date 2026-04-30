package realtime

import (
	"testing"
	"time"
)

func TestShouldPoll(t *testing.T) {
	if ShouldPoll(true) {
		t.Fatalf("expected no polling when socket is healthy")
	}
	if !ShouldPoll(false) {
		t.Fatalf("expected polling when socket is unhealthy")
	}
}

func TestNextFallbackInterval(t *testing.T) {
	interval := NextFallbackInterval(0)
	if interval != 5*time.Second {
		t.Fatalf("expected first interval to be 5s, got %v", interval)
	}

	interval = NextFallbackInterval(interval)
	if interval != 10*time.Second {
		t.Fatalf("expected second interval to be 10s, got %v", interval)
	}

	interval = NextFallbackInterval(20 * time.Second)
	if interval != 30*time.Second {
		t.Fatalf("expected capped interval to be 30s, got %v", interval)
	}

	interval = NextFallbackInterval(30 * time.Second)
	if interval != 30*time.Second {
		t.Fatalf("expected cap to stay 30s, got %v", interval)
	}
}
