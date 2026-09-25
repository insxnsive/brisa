package auth

import (
	"bytes"
	"runtime"
	"testing"
)

func TestSessionEnvelopePlatformPolicy(t *testing.T) {
	for _, raw := range [][]byte{
		[]byte(encryptedSessionHeader + "synthetic"),
		[]byte(darwinSessionHeader + "synthetic"),
	} {
		_, _, err := openSessionPayload(raw)
		if runtime.GOOS == "windows" && bytes.HasPrefix(raw, []byte(encryptedSessionHeader)) {
			continue
		}
		if runtime.GOOS == "darwin" && bytes.HasPrefix(raw, []byte(darwinSessionHeader)) {
			continue
		}
		if err == nil {
			t.Fatal("accepted another platform's encrypted envelope")
		}
	}
	if runtime.GOOS == "darwin" {
		if _, _, err := openSessionPayload([]byte(`{"session":"synthetic"}`)); err == nil {
			t.Fatal("accepted plaintext on Darwin")
		}
	}
}
