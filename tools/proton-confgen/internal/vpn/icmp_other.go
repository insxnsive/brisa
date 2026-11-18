//go:build !windows

package vpn

import (
	"context"
	"errors"
)

// The in-process ICMP path is Windows-only; other systems keep the system ping command.
func fastIcmpPing(context.Context, string) (int, error) {
	return 0, errors.New("in-process ICMP is not available on this platform")
}
