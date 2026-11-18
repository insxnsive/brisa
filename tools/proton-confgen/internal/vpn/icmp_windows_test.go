//go:build windows

package vpn

import (
	"context"
	"testing"
	"time"
)

func TestFastIcmpLocalhost(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	ms, err := fastIcmpPing(ctx, "127.0.0.1")
	if err != nil {
		t.Fatalf("in-process ICMP failed on loopback: %v", err)
	}
	if ms < 1 || ms > 50 {
		t.Fatalf("loopback RTT out of range: %d ms", ms)
	}
}
