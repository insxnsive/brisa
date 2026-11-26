// Package constants defines constants used throughout the application.
package constants

import "runtime"

// API endpoints
// Paths match the ProtonVPN Linux reference client (python-proton-core/python-proton-vpn-api-core).
const (
	DefaultAPIURL   = "https://vpn-api.proton.me"
	AuthInfoPath    = "/auth/info"
	AuthPath        = "/auth"
	TwoFAPath       = "/auth/2fa"
	RefreshPath     = "/auth/refresh"
	VPNSettingsPath = "/vpn/v2"
	CertificatePath = "/vpn/v1/certificate"
	LogicalsPath    = "/vpn/v1/logicals"
	// CaptchaPath serves the human verification widget for this API entry point.
	CaptchaPath = "/core/v4/captcha"
)

// ClientVersion is the official ProtonVPN Linux client version sent in the app
// version header. Source: https://raw.githubusercontent.com/ProtonVPN/python-proton-vpn-api-core/stable/versions.yml
// Proton began rejecting linux-vpn@4.17.2 with HTTP 422 code 5003 on 2026-11-19.
var ClientVersion = "6.1.2"

// API version headers are filled in init when not overridden at build time via
// ldflags: -X .../internal/constants.ClientVersion=X.Y.Z
var (
	AppVersion = ""
	UserAgent  = ""
)

func init() {
	if AppVersion == "" {
		AppVersion = "linux-vpn-gui@" + ClientVersion + "+" + officialArch(runtime.GOARCH)
	}
	if UserAgent == "" {
		UserAgent = "ProtonVPN/" + ClientVersion + " (Linux; ubuntu/26.04)"
	}
}

func officialArch(goarch string) string {
	switch goarch {
	case "amd64":
		return "x86-64"
	case "arm64":
		return "aarch64"
	case "arm":
		return "armv7l"
	default:
		return goarch
	}
}

// API response codes
// Reference: proton-python-client/proton/api.py checks for codes 1000 and 1001
const (
	APICodeSuccess     = 1000
	APICodeMultiStatus = 1001 // Also indicates success in some contexts
)

// IsSuccessCode checks if an API response code indicates success
func IsSuccessCode(code int) bool {
	return code == APICodeSuccess || code == APICodeMultiStatus
}

// Server/feature status values
const (
	StatusOnline = 1
	EnabledTrue  = 1
)
