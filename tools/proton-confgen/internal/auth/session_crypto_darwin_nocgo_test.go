//go:build darwin && !cgo

package auth

import "testing"

func TestDarwinWithoutCgoFailsClosed(t *testing.T) {
	if _, err := sealSessionPayload([]byte(`{"session":"synthetic"}`)); err == nil {
		t.Fatal("saved without Keychain")
	}
	if _, _, err := openSessionPayload([]byte(darwinSessionHeader + "synthetic")); err == nil {
		t.Fatal("loaded without Keychain")
	}
}
