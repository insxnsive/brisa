package auth

import (
	"bytes"
	"testing"
)

func TestSessionCipher(t *testing.T) {
	key := bytes.Repeat([]byte{0x41}, 32)
	plaintext := []byte(`{"session":"synthetic"}`)
	one, err := encryptSessionWithKey(key, plaintext)
	if err != nil {
		t.Fatal(err)
	}
	two, err := encryptSessionWithKey(key, plaintext)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Equal(one, two) {
		t.Fatal("nonce was reused")
	}
	opened, err := decryptSessionWithKey(key, one)
	if err != nil || !bytes.Equal(opened, plaintext) {
		t.Fatalf("roundtrip failed: %v", err)
	}
	for _, tc := range []struct {
		name            string
		ciphertext, key []byte
	}{
		{"tamper", append([]byte(nil), one...), key},
		{"wrong key", one, bytes.Repeat([]byte{0x42}, 32)},
		{"truncated", one[:12], key},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if tc.name == "tamper" {
				tc.ciphertext[len(tc.ciphertext)-1] ^= 1
			}
			if _, err := decryptSessionWithKey(tc.key, tc.ciphertext); err == nil {
				t.Fatal("accepted invalid ciphertext")
			}
		})
	}
	if _, err := encryptSessionWithKey(key[:31], plaintext); err == nil {
		t.Fatal("accepted short key")
	}
}
