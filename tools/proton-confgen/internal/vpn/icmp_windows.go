//go:build windows

package vpn

import (
	"context"
	"encoding/binary"
	"net"
	"syscall"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// In-process ICMP through iphlpapi. Spawning ping.exe per IP dominates route scans on Windows,
// and the API needs no administrator rights and no extra process.
var (
	iphlpapi            = windows.NewLazySystemDLL("iphlpapi.dll")
	procIcmpCreateFile  = iphlpapi.NewProc("IcmpCreateFile")
	procIcmpSendEcho    = iphlpapi.NewProc("IcmpSendEcho")
	procIcmpCloseHandle = iphlpapi.NewProc("IcmpCloseHandle")
)

func fastIcmpPing(ctx context.Context, ip string) (int, error) {
	if ctx.Err() != nil {
		return 0, ctx.Err()
	}
	addr := net.ParseIP(ip).To4()
	if addr == nil {
		return 0, syscall.EINVAL
	}
	timeout := candidateProbeTimeout
	if deadline, ok := ctx.Deadline(); ok {
		remaining := time.Until(deadline)
		if remaining <= 0 {
			return 0, context.DeadlineExceeded
		}
		if remaining < timeout {
			timeout = remaining
		}
	}
	handle, _, callErr := procIcmpCreateFile.Call()
	if handle == 0 || handle == uintptr(syscall.InvalidHandle) {
		return 0, callErr
	}
	defer procIcmpCloseHandle.Call(handle)

	destination := binary.LittleEndian.Uint32(addr)
	payload := [32]byte{'b', 'r', 'i', 's', 'a'}
	reply := make([]byte, 64+len(payload))
	milliseconds := uint32(timeout / time.Millisecond)
	if milliseconds == 0 {
		milliseconds = 1
	}
	ok, _, sendErr := procIcmpSendEcho.Call(
		handle,
		uintptr(destination),
		uintptr(unsafe.Pointer(&payload[0])),
		uintptr(len(payload)),
		0,
		uintptr(unsafe.Pointer(&reply[0])),
		uintptr(len(reply)),
		uintptr(milliseconds),
	)
	if ok == 0 {
		return 0, sendErr
	}
	// ICMP_ECHO_REPLY: Address at 0, Status at 4, RoundTripTime at 8. Status 0 means success.
	if binary.LittleEndian.Uint32(reply[4:8]) != 0 {
		return 0, syscall.EIO
	}
	rtt := binary.LittleEndian.Uint32(reply[8:12])
	if rtt == 0 {
		rtt = 1
	}
	return int(rtt), nil
}
