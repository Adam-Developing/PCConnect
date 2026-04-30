package realtime

import "time"

const (
	BaseFallbackInterval = 5 * time.Second
	MaxFallbackInterval  = 30 * time.Second
)

func NextFallbackInterval(previous time.Duration) time.Duration {
	if previous <= 0 {
		return BaseFallbackInterval
	}

	next := previous * 2
	if next > MaxFallbackInterval {
		return MaxFallbackInterval
	}

	return next
}

func ShouldPoll(socketHealthy bool) bool {
	return !socketHealthy
}
