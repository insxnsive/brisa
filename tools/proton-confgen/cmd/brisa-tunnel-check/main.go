package main

import (
	"context"
	"fmt"
	"io"
	"os"

	"protonvpn-wg-confgen/internal/macengine"
)

const success = `{"schemaVersion":1,"scope":"loopback-only","tcp":true,"udp":true,"dns":true,"shutdown":true}`

func run(args []string, out, errout io.Writer) int {
	if len(args) != 1 || args[0] != "--self-test" {
		fmt.Fprintln(errout, "usage: brisa-tunnel-check --self-test")
		return 2
	}
	if err := macengine.SelfTest(context.Background()); err != nil {
		fmt.Fprintln(errout, "self-test failed")
		return 1
	}
	fmt.Fprintln(out, success)
	return 0
}

func main() { os.Exit(run(os.Args[1:], os.Stdout, os.Stderr)) }
