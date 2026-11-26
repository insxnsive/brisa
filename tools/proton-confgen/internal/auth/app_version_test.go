package auth

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"

	"protonvpn-wg-confgen/internal/api"
	"protonvpn-wg-confgen/internal/config"
)

func TestAuthenticationErrorMapsAppVersionCodes(t *testing.T) {
	for _, code := range []int{CodeAppVersionMissing, CodeAppVersionBad, CodeAPIVersionInvalid} {
		if err := newAuthenticationError(code); !IsAppVersionError(err) {
			t.Fatalf("newAuthenticationError(%d) = %T; want AppVersionError", code, err)
		}
		if err := newTwoFactorError(code); !IsAppVersionError(err) {
			t.Fatalf("newTwoFactorError(%d) = %T; want AppVersionError", code, err)
		}
		if err := appVersionRejection([]byte(fmt.Sprintf(`{"Code":%d}`, code))); !IsAppVersionError(err) {
			t.Fatalf("appVersionRejection(%d) = %T; want AppVersionError", code, err)
		}
	}
	if !IsTwoFactorError(newTwoFactorError(9100)) {
		t.Fatal("ordinary 2FA code lost its TwoFactorError type")
	}
	if appVersionRejection([]byte(`{"Code":8002}`)) != nil {
		t.Fatal("non-version code became an app version error")
	}
}

func TestAppVersionErrorsCarryUpdateMessage(t *testing.T) {
	err := &AppVersionError{Code: CodeAppVersionBad}
	if !containsAll(err.Error(), "Proton", "5003", "Brisa") {
		t.Fatalf("message = %q", err.Error())
	}
	if (&AppVersionError{}).Error() != "Proton no longer accepts this app version; update Brisa" {
		t.Fatalf("zero-code message = %q", (&AppVersionError{}).Error())
	}
}

func TestAuthInfoAndTwoFAReturnAppVersionError(t *testing.T) {
	for _, code := range []int{CodeAppVersionMissing, CodeAppVersionBad, CodeAPIVersionInvalid} {
		t.Run(fmt.Sprintf("code_%d", code), func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				switch r.URL.Path {
				case "/auth/info":
					w.WriteHeader(http.StatusUnprocessableEntity)
					_, _ = fmt.Fprintf(w, `{"Code":%d}`, code)
				case "/auth/2fa":
					w.WriteHeader(http.StatusUnprocessableEntity)
					_, _ = fmt.Fprintf(w, `{"Code":%d}`, code)
				default:
					http.NotFound(w, r)
				}
			}))
			defer server.Close()
			client := NewClient(&config.Config{APIURL: server.URL, Username: "synthetic@example.com"})

			if _, err := client.getAuthInfoContext(context.Background()); !IsAppVersionError(err) {
				t.Fatalf("auth info error = %T %v; want AppVersionError", err, err)
			}
			if _, err := client.submit2FA(&api.Session{AccessToken: "synthetic-access", UID: "synthetic-uid"}, "123456"); !IsAppVersionError(err) {
				t.Fatalf("2FA error = %T %v; want AppVersionError", err, err)
			}
		})
	}
}

func TestAuthEndpointReturnsAppVersionError(t *testing.T) {
	for _, code := range []int{CodeAppVersionMissing, CodeAppVersionBad, CodeAPIVersionInvalid} {
		t.Run(fmt.Sprintf("code_%d", code), func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Path != "/auth" {
					http.NotFound(w, r)
					return
				}
				w.WriteHeader(http.StatusUnprocessableEntity)
				_, _ = fmt.Fprintf(w, `{"Code":%d}`, code)
			}))
			defer server.Close()
			client := NewClient(&config.Config{APIURL: server.URL, Username: "synthetic@example.com"})

			if _, err := client.sendAuthRequestContext(context.Background(), map[string]any{"Username": "synthetic"}); !IsAppVersionError(err) {
				t.Fatalf("auth error = %T %v; want AppVersionError", err, err)
			}
		})
	}
}

func TestRefreshSessionPreservesAppVersionRejection(t *testing.T) {
	for _, code := range []int{CodeAppVersionMissing, CodeAppVersionBad, CodeAPIVersionInvalid} {
		for _, status := range []int{http.StatusUnprocessableEntity, http.StatusUnauthorized, http.StatusForbidden, http.StatusOK, http.StatusServiceUnavailable} {
			t.Run(fmt.Sprintf("code_%d_HTTP_%d", code, status), func(t *testing.T) {
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					w.WriteHeader(status)
					_, _ = fmt.Fprintf(w, `{"Code":%d}`, code)
				}))
				defer server.Close()

				_, err := RefreshSessionContext(context.Background(), server.Client(), server.URL, auditSession("access", "refresh", 3600))
				if !IsAppVersionError(err) {
					t.Fatalf("RefreshSessionContext() error = %T %v; want AppVersionError", err, err)
				}
			})
		}
	}
}

func TestVerifySessionStatusPreservesAppVersionRejection(t *testing.T) {
	for _, code := range []int{CodeAppVersionMissing, CodeAppVersionBad, CodeAPIVersionInvalid} {
		for _, status := range []int{http.StatusUnprocessableEntity, http.StatusUnauthorized, http.StatusOK} {
			t.Run(fmt.Sprintf("code_%d_HTTP_%d", code, status), func(t *testing.T) {
				server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
					w.WriteHeader(status)
					_, _ = fmt.Fprintf(w, `{"Code":%d}`, code)
				}))
				defer server.Close()

				valid, err := VerifySessionStatusContext(context.Background(), server.Client(), server.URL, auditSession("access", "refresh", 3600))
				if valid || !IsAppVersionError(err) {
					t.Fatalf("VerifySessionStatusContext() = %v, %T %v; want false, AppVersionError", valid, err, err)
				}
			})
		}
	}
}

func TestVerifySessionStatusStillRejectsRealUnauthorized(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusUnauthorized)
		_, _ = fmt.Fprint(w, `{"Code":8002}`)
	}))
	defer server.Close()

	valid, err := VerifySessionStatusContext(context.Background(), server.Client(), server.URL, auditSession("access", "refresh", 3600))
	if valid || !IsSessionInvalid(err) {
		t.Fatalf("VerifySessionStatusContext() = %v, %T %v; want SessionInvalidError", valid, err, err)
	}
}

func TestAuthenticateAndCheckSessionKeepFileOnAppVersionError(t *testing.T) {
	for _, operation := range []string{"authenticate", "check-session"} {
		t.Run(operation, func(t *testing.T) {
			root := t.TempDir()
			file := filepath.Join(root, "proton-session.json")
			saved := auditSession("access-old", "refresh-old", 30*24*60*60)
			if err := NewSessionStore(file).Save(saved, "audit@example.com", 30*24*time.Hour); err != nil {
				t.Fatalf("Save() error = %v", err)
			}

			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Path != "/vpn/v1/logicals" {
					t.Fatalf("unexpected fresh-auth request: %s", r.URL.Path)
				}
				w.WriteHeader(http.StatusUnprocessableEntity)
				_, _ = fmt.Fprint(w, `{"Code":5003}`)
			}))
			defer server.Close()

			client := NewClient(&config.Config{
				APIURL:      server.URL,
				Username:    "audit@example.com",
				Password:    "not-a-real-password",
				SessionFile: file,
			})
			var err error
			if operation == "authenticate" {
				_, err = client.Authenticate()
			} else {
				_, _, err = client.CheckSession()
			}
			if !IsAppVersionError(err) {
				t.Fatalf("%s error = %T %v; want AppVersionError", operation, err, err)
			}
			if _, statErr := os.Stat(file); statErr != nil {
				t.Fatalf("saved session removed after app version rejection: %v", statErr)
			}
			loaded, _, loadErr := NewSessionStore(file).Load("audit@example.com")
			if loadErr != nil || loaded == nil || loaded.RefreshToken != "refresh-old" {
				t.Fatalf("saved session changed: %#v, %v", loaded, loadErr)
			}
		})
	}
}

func containsAll(value string, parts ...string) bool {
	for _, part := range parts {
		if !contains(value, part) {
			return false
		}
	}
	return true
}

func contains(value, part string) bool {
	return len(part) == 0 || (len(value) >= len(part) && indexOf(value, part) >= 0)
}

func indexOf(value, part string) int {
	for i := 0; i+len(part) <= len(value); i++ {
		if value[i:i+len(part)] == part {
			return i
		}
	}
	return -1
}
