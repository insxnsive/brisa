package api

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestSetHumanVerification(t *testing.T) {
	tests := []struct {
		name      string
		token     string
		method    string
		wantToken string
		wantType  string
	}{
		{
			name: "empty token leaves the request untouched",
		},
		{
			name:      "token and method are replayed verbatim",
			token:     "abc123",
			method:    "captcha",
			wantToken: "abc123",
			wantType:  "captcha",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			req, err := http.NewRequest(http.MethodPost, "https://example.invalid", http.NoBody)
			if err != nil {
				t.Fatal(err)
			}
			SetHumanVerification(req, tt.token, tt.method)

			if got := req.Header.Get(hvTokenHeader); got != tt.wantToken {
				t.Errorf("%s = %q, want %q", hvTokenHeader, got, tt.wantToken)
			}
			if got := req.Header.Get(hvTokenTypeHeader); got != tt.wantType {
				t.Errorf("%s = %q, want %q", hvTokenTypeHeader, got, tt.wantType)
			}
		})
	}
}

func TestDoDoesNotEchoMalformedResponseBody(t *testing.T) {
	const sensitive = "synthetic-challenge-token"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusBadGateway)
		_, _ = fmt.Fprintf(w, "not-json %s", sensitive)
	}))
	defer server.Close()

	req, err := http.NewRequest(http.MethodGet, server.URL, http.NoBody)
	if err != nil {
		t.Fatal(err)
	}
	err = Do(server.Client(), req, &map[string]any{})
	if err == nil || strings.Contains(err.Error(), sensitive) {
		t.Fatalf("Do() error = %q; want a body-redacted protocol error", err)
	}
}

func TestDoRejectsOversizedResponse(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprint(w, `{"Code":1000,"Padding":"`)
		_, _ = fmt.Fprint(w, strings.Repeat("x", 17<<20))
		_, _ = fmt.Fprint(w, `"}`)
	}))
	defer server.Close()

	req, err := http.NewRequest(http.MethodGet, server.URL, http.NoBody)
	if err != nil {
		t.Fatal(err)
	}
	err = Do(server.Client(), req, &map[string]any{})
	if err == nil {
		t.Fatal("Do() accepted an oversized response")
	}
}
