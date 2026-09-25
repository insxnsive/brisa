//go:build darwin && cgo

package auth

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
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

func TestNativeKeychainAcrossProcesses(t *testing.T) {
	const value = "synthetic process-restart fixture"
	if service := os.Getenv("BRISA_TEST_KEYCHAIN_SERVICE"); service != "" {
		if !strings.HasPrefix(service, "dev.insxnsive.brisa.test.") {
			t.Fatal("refusing a non-disposable Keychain target")
		}
		ownTarget := activeSessionKeychainTarget
		activeSessionKeychainTarget = sessionKeychainTarget{service: service, account: ownTarget.account}
		defer func() { activeSessionKeychainTarget = ownTarget }()
		raw, err := os.ReadFile(os.Getenv("BRISA_TEST_ENCRYPTED_FIXTURE"))
		if err != nil {
			t.Fatal(err)
		}
		opened, encrypted, err := openSessionPayload(raw)
		if err != nil || !encrypted || string(opened) != value {
			t.Fatalf("Keychain-backed fixture did not survive process restart: %v", err)
		}
		return
	}
	sealed, err := sealSessionPayload([]byte(value))
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(sealed, []byte(value)) {
		t.Fatal("plaintext appeared in encrypted fixture")
	}
	file := filepath.Join(t.TempDir(), "fixture.enc")
	if err := os.WriteFile(file, sealed, 0o600); err != nil {
		t.Fatal(err)
	}
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	child := exec.CommandContext(ctx, executable, "-test.run=^TestNativeKeychainAcrossProcesses$")
	child.Env = append(os.Environ(), "BRISA_TEST_KEYCHAIN_SERVICE="+activeSessionKeychainTarget.service, "BRISA_TEST_ENCRYPTED_FIXTURE="+file)
	if output, err := child.CombinedOutput(); err != nil {
		t.Fatalf("owned fixture process failed: %v; %s", err, output)
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
