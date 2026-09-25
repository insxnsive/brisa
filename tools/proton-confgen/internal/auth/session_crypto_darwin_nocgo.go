//go:build darwin && !cgo

package auth

import "fmt"

func sessionStorageUsesEncryption() bool { return true }
func protectSessionBytes([]byte) ([]byte, error) {
	return nil, fmt.Errorf("macOS Keychain support unavailable")
}
func unprotectSessionBytes([]byte) ([]byte, error) {
	return nil, fmt.Errorf("macOS Keychain support unavailable")
}
