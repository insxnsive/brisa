package vpn

import (
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"

	"protonvpn-wg-confgen/internal/auth"
	"protonvpn-wg-confgen/internal/config"
)

func TestCertificateFeatures(t *testing.T) {
	tests := []struct {
		name               string
		cfg                config.Config
		wantRandomNAT      bool
		wantPortForwarding bool
	}{
		{
			name:          "strict NAT by default",
			wantRandomNAT: true,
		},
		{
			name:          "moderate NAT disables random NAT",
			cfg:           config.Config{ModerateNAT: true},
			wantRandomNAT: false,
		},
		{
			name:               "port forwarding",
			cfg:                config.Config{PortForwarding: true},
			wantRandomNAT:      true,
			wantPortForwarding: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			client := NewClient(&tt.cfg, nil)
			features := client.certificateFeatures()

			if got := features["RandomNAT"]; got != tt.wantRandomNAT {
				t.Errorf("RandomNAT = %v, want %v", got, tt.wantRandomNAT)
			}
			if got := features["PortForwarding"]; got != tt.wantPortForwarding {
				t.Errorf("PortForwarding = %v, want %v", got, tt.wantPortForwarding)
			}
		})
	}
}

func TestRequestCertificatePropagatesHumanVerificationChallenge(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusUnprocessableEntity)
		_, _ = fmt.Fprint(w, `{"Code":9001,"Error":"human verification required","Details":{"HumanVerificationToken":"fresh-challenge","HumanVerificationMethods":["captcha"]}}`)
	}))
	defer server.Close()

	client := NewClient(&config.Config{APIURL: server.URL}, nil)
	_, err := client.requestCertificate(map[string]any{"ClientPublicKey": "synthetic"})
	var hvErr auth.HumanVerificationError
	if !errors.As(err, &hvErr) {
		t.Fatalf("requestCertificate() error = %T %v; want HumanVerificationError", err, err)
	}
	if hvErr.Code != "CAPTCHA_REQUIRED" || !hvErr.Retryable {
		t.Fatalf("human verification classification = %+v", hvErr)
	}
	parsed, parseErr := url.Parse(hvErr.CaptchaURL)
	if parseErr != nil || parsed.Query().Get("Token") != "fresh-challenge" {
		t.Fatalf("captcha URL = %q, parse error = %v", hvErr.CaptchaURL, parseErr)
	}
}

func TestRequestCertificateReplaysSelectedHumanVerificationToCertificateEndpoint(t *testing.T) {
	const solved = "opaque-ownership-token"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.Header.Get("x-pm-human-verification-token"); got != solved {
			t.Errorf("human verification token header = %q, want solved token", got)
		}
		if got := r.Header.Get("x-pm-human-verification-token-type"); got != "ownership-sms" {
			t.Errorf("human verification type header = %q, want ownership-sms", got)
		}
		_, _ = fmt.Fprint(w, `{"Code":1000,"SerialNumber":"synthetic"}`)
	}))
	defer server.Close()

	client := NewClient(&config.Config{APIURL: server.URL, HVToken: solved, HVMethod: "ownership-sms"}, nil)
	info, err := client.requestCertificate(map[string]any{"ClientPublicKey": "synthetic"})
	if err != nil || info == nil || info.SerialNumber != "synthetic" {
		t.Fatalf("requestCertificate() = %#v, %v; want successful replay", info, err)
	}
}

func TestRequestCertificateRejectsReplayedChallengeWithFreshTypedChallenge(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusUnprocessableEntity)
		_, _ = fmt.Fprint(w, `{"Code":9001,"Details":{"HumanVerificationToken":"replacement-challenge"}}`)
	}))
	defer server.Close()

	client := NewClient(&config.Config{APIURL: server.URL, HVToken: "old:answer"}, nil)
	_, err := client.requestCertificate(map[string]any{"ClientPublicKey": "synthetic"})
	var hvErr auth.HumanVerificationError
	if !errors.As(err, &hvErr) || hvErr.Code != "CAPTCHA_INVALID" {
		t.Fatalf("requestCertificate() error = %T %v; want CAPTCHA_INVALID", err, err)
	}
	parsed, parseErr := url.Parse(hvErr.CaptchaURL)
	if parseErr != nil || parsed.Query().Get("Token") != "replacement-challenge" {
		t.Fatalf("replacement captcha URL = %q, parse error = %v", hvErr.CaptchaURL, parseErr)
	}
}

func TestRequestCertificateValidatesHTTPStatusBeforeAcceptingSuccessCode(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusUnauthorized)
		_, _ = fmt.Fprint(w, `{"Code":1000,"SerialNumber":"must-not-be-accepted"}`)
	}))
	defer server.Close()

	client := NewClient(&config.Config{APIURL: server.URL}, nil)
	info, err := client.requestCertificate(map[string]any{"ClientPublicKey": "synthetic"})
	var protocolErr *auth.ProtocolError
	if info != nil || !errors.As(err, &protocolErr) || protocolErr.StatusCode != http.StatusUnauthorized {
		t.Fatalf("requestCertificate() = %#v, %T %v; want HTTP 401 ProtocolError", info, err, err)
	}
}

func TestRequestCertificateClassifiesTemporaryHTTPStatus(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
		_, _ = fmt.Fprint(w, `{"Code":1000}`)
	}))
	defer server.Close()

	client := NewClient(&config.Config{APIURL: server.URL}, nil)
	_, err := client.requestCertificate(map[string]any{"ClientPublicKey": "synthetic"})
	if !auth.IsTemporarySessionError(err) {
		t.Fatalf("requestCertificate() error = %T %v; want TemporarySessionError", err, err)
	}
}
