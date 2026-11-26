package constants

import (
	"regexp"
	"strings"
	"testing"
)

func TestAppVersionMatchesOfficialLinuxClient(t *testing.T) {
	if !strings.HasPrefix(AppVersion, "linux-vpn-gui@"+ClientVersion+"+") {
		t.Fatalf("AppVersion = %q; want linux-vpn-gui@%s+<arch>", AppVersion, ClientVersion)
	}
	if strings.HasPrefix(AppVersion, "linux-vpn@") {
		t.Fatalf("AppVersion = %q; retired linux-vpn form rejected", AppVersion)
	}
	if !regexp.MustCompile(`^linux-vpn-gui@\d+\.\d+\.\d+\+[a-z0-9-]+$`).MatchString(AppVersion) {
		t.Fatalf("AppVersion = %q; want linux-vpn-gui@X.Y.Z+<arch>", AppVersion)
	}
	if UserAgent != "ProtonVPN/"+ClientVersion+" (Linux; ubuntu/26.04)" {
		t.Fatalf("UserAgent = %q", UserAgent)
	}
}

func TestOfficialArch(t *testing.T) {
	tests := map[string]string{
		"amd64":   "x86-64",
		"arm64":   "aarch64",
		"arm":     "armv7l",
		"riscv64": "riscv64",
	}
	for goarch, want := range tests {
		if got := officialArch(goarch); got != want {
			t.Fatalf("officialArch(%q) = %q; want %q", goarch, got, want)
		}
	}
}
