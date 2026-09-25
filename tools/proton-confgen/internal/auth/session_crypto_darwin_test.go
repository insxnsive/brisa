//go:build darwin && cgo

package auth

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"protonvpn-wg-confgen/internal/api"
)

func TestMain(m *testing.M) {
	var id [16]byte
	if _, err := rand.Read(id[:]); err != nil {
		os.Exit(1)
	}
	activeSessionKeychainTarget = sessionKeychainTarget{
		service: "dev.insxnsive.brisa.test." + hex.EncodeToString(id[:]),
		account: "Disposable Brisa test key",
	}
	code := m.Run()
	deleteDisposableSessionKey(activeSessionKeychainTarget)
	os.Exit(code)
}

func TestNativeKeychainSession(t *testing.T) {
	target := activeSessionKeychainTarget
	activeSessionKeychainTarget = sessionKeychainTarget{service: target.service + ".missing", account: target.account}
	_, missingErr := sessionKey(false)
	activeSessionKeychainTarget = target
	if missingErr == nil {
		t.Fatal("missing key was created on decrypt")
	}
	sealed, err := sealSessionPayload([]byte(`{"session":"synthetic"}`))
	if err != nil {
		t.Fatalf("Keychain integration unavailable: %v", err)
	}
	if !bytes.HasPrefix(sealed, []byte(darwinSessionHeader)) {
		t.Fatal("wrong envelope")
	}
	opened, encrypted, err := openSessionPayload(sealed)
	if err != nil || !encrypted || string(opened) != `{"session":"synthetic"}` {
		t.Fatalf("native roundtrip failed: %v", err)
	}
	key, err := sessionKey(false)
	if err != nil || len(key) != 32 {
		t.Fatalf("Keychain key missing or malformed: %v", err)
	}
	clear(key)
}

func TestNativeKeychainRejectsMalformedExistingKey(t *testing.T) {
	target := activeSessionKeychainTarget
	malformed := sessionKeychainTarget{service: target.service + ".malformed", account: target.account}
	activeSessionKeychainTarget = malformed
	defer func() { activeSessionKeychainTarget = target; deleteDisposableSessionKey(malformed) }()
	if status := keychainAdd(malformed, []byte("short")); status != 0 {
		t.Fatalf("add malformed test key failed: %d", status)
	}
	if _, err := protectSessionBytes([]byte("synthetic")); err == nil {
		t.Fatal("overwrote malformed existing key")
	}
}

func TestNativeKeychainConcurrentCreateUsesOneKey(t *testing.T) {
	target := activeSessionKeychainTarget
	concurrent := sessionKeychainTarget{service: target.service + ".concurrent", account: target.account}
	activeSessionKeychainTarget = concurrent
	defer func() { activeSessionKeychainTarget = target; deleteDisposableSessionKey(concurrent) }()
	var group sync.WaitGroup
	keys := make(chan []byte, 8)
	errs := make(chan error, 8)
	for range 8 {
		group.Add(1)
		go func() {
			defer group.Done()
			key, err := sessionKey(true)
			if err != nil {
				errs <- err
			} else {
				keys <- key
			}
		}()
	}
	group.Wait()
	close(keys)
	close(errs)
	for err := range errs {
		t.Fatalf("concurrent creation failed: %v", err)
	}
	var first []byte
	for key := range keys {
		if first == nil {
			first = key
		} else if !bytes.Equal(first, key) {
			t.Fatal("concurrent creation returned different keys")
		}
		defer clear(key)
	}
}

func TestDarwinSessionDirectoryPrivate(t *testing.T) {
	directory := filepath.Join(t.TempDir(), "sessions")
	store := NewSessionStore(filepath.Join(directory, "session.json"))
	if err := store.Save(&api.Session{AccessToken: "synthetic", UID: "synthetic", ExpiresIn: 3600}, "synthetic", time.Hour); err != nil {
		t.Fatal(err)
	}
	info, err := os.Stat(directory)
	if err != nil {
		t.Fatal(err)
	}
	if info.Mode().Perm() != 0o700 {
		t.Fatalf("directory mode = %o", info.Mode().Perm())
	}
}
