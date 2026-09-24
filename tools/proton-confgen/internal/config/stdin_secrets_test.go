package config

import (
	"strings"
	"testing"
)

func TestReadStdinSecrets(t *testing.T) {
	cfg := &Config{StdinSecrets: true}
	input := `{"password":"p a s s","twoFactorCode":"123456","humanVerificationToken":"challenge:answer"}`
	if err := readStdinSecrets(strings.NewReader(input), cfg); err != nil {
		t.Fatalf("readStdinSecrets() error = %v", err)
	}
	if cfg.Password != "p a s s" || cfg.TwoFactorCode != "123456" || cfg.HVToken != "challenge:answer" {
		t.Fatalf("secrets were not populated correctly: %+v", cfg)
	}
}

func TestReadStdinSecretsRejectsMalformedInputWithoutEchoingIt(t *testing.T) {
	cfg := &Config{StdinSecrets: true}
	err := readStdinSecrets(strings.NewReader(`{"password":"super-secret`), cfg)
	if err == nil {
		t.Fatal("expected malformed input to fail")
	}
	if strings.Contains(err.Error(), "super-secret") {
		t.Fatalf("error echoed a secret: %v", err)
	}
}

func TestReadStdinSecretsAllowsCertificateReplayWithoutPassword(t *testing.T) {
	cfg := &Config{StdinSecrets: true}
	if err := readStdinSecrets(strings.NewReader(`{"humanVerificationToken":"synthetic-challenge:answer"}`), cfg); err != nil {
		t.Fatalf("saved-session certificate replay was rejected: %v", err)
	}
	if cfg.HVToken != "synthetic-challenge:answer" || cfg.Password != "" {
		t.Fatal("certificate replay did not preserve token-only credentials")
	}
}

func TestReadStdinSecretsRequiresPasswordForLoginEvenWithChallenge(t *testing.T) {
	cfg := &Config{StdinSecrets: true, LoginOnly: true}
	if err := readStdinSecrets(strings.NewReader(`{"humanVerificationToken":"synthetic-challenge:answer"}`), cfg); err == nil {
		t.Fatal("interactive login accepted a challenge without password")
	}
}

func TestReadStdinSecretsRequiresPassword(t *testing.T) {
	cfg := &Config{StdinSecrets: true}
	if err := readStdinSecrets(strings.NewReader(`{"twoFactorCode":"123456"}`), cfg); err == nil {
		t.Fatal("expected missing password to fail")
	}
}

func TestReadStdinSecretsAcceptsHumanVerificationMethod(t *testing.T) {
	cfg := &Config{StdinSecrets: true, HVMethod: "captcha"}
	err := readStdinSecrets(strings.NewReader(`{"humanVerificationToken":"opaque-token","humanVerificationMethod":"ownership-sms"}`), cfg)
	if err != nil {
		t.Fatalf("valid human verification method was rejected: %v", err)
	}
	if cfg.HVMethod != "ownership-sms" || cfg.HVToken != "opaque-token" {
		t.Fatalf("typed replay = method %q token %q", cfg.HVMethod, cfg.HVToken)
	}
}

func TestReadStdinSecretsRejectsInvalidHumanVerificationMethod(t *testing.T) {
	cfg := &Config{StdinSecrets: true, HVMethod: "captcha"}
	err := readStdinSecrets(strings.NewReader(`{"password":"synthetic","humanVerificationMethod":"security-key"}`), cfg)
	if err == nil || strings.Contains(err.Error(), "security-key") {
		t.Fatalf("invalid method error = %q; want redacted validation failure", err)
	}
}
