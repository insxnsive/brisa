package macengine

import (
	"bytes"
	"context"
	"crypto/ecdh"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"io"
	"net"
	"net/netip"
	"strconv"
	"sync"
	"time"

	"golang.org/x/net/dns/dnsmessage"
)

var testServerIP = netip.MustParseAddr("10.70.0.1")
var testClientIP = netip.MustParseAddr("10.70.0.2")
var testServerIP6 = netip.MustParseAddr("fd70::1")
var testClientIP6 = netip.MustParseAddr("fd70::2")

const testName = "brisa-check.invalid"
const testV6OnlyName = "brisa-v6-only.invalid"

// SelfTest exercises encrypted WireGuard traffic between disposable loopback
// peers. It never reads configuration, uses external endpoints, or creates a
// host tunnel interface.
func SelfTest(parent context.Context) error {
	if err := selfTestIPv4(parent); err != nil {
		return err
	}
	return selfTestIPv6(parent)
}

func selfTestIPv4(parent context.Context) error {
	if parent == nil {
		return errors.New("nil context")
	}
	ctx, cancel := context.WithTimeout(parent, 20*time.Second)
	defer cancel()
	if err := ctx.Err(); err != nil {
		return errors.New("self-test cancelled")
	}
	serverKey, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		return errors.New("self-test key generation failed")
	}
	clientKey, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		return errors.New("self-test key generation failed")
	}
	encode := func(b []byte) string { return base64.StdEncoding.EncodeToString(b) }
	server, err := newEngine(Config{PrivateKey: encode(serverKey.Bytes()), PeerPublicKey: encode(clientKey.PublicKey().Bytes()), Endpoint: netip.MustParseAddrPort("127.0.0.1:1"), Address: testServerIP, AllowedIP: netip.MustParsePrefix("10.70.0.2/32"), MTU: 1280}, &loopbackBind{})
	if err != nil {
		return errors.New("self-test server failed")
	}
	defer server.Close()
	client, err := newEngine(Config{PrivateKey: encode(clientKey.Bytes()), PeerPublicKey: encode(serverKey.PublicKey().Bytes()), Endpoint: netip.AddrPortFrom(netip.MustParseAddr("127.0.0.1"), server.ListenPort()), Address: testClientIP, AllowedIP: netip.MustParsePrefix("10.70.0.1/32"), DNS: []netip.Addr{testServerIP}, MTU: 1280}, &loopbackBind{})
	if err != nil {
		return errors.New("self-test client failed")
	}
	defer client.Close()
	tcpListener, err := server.net.ListenTCPAddrPort(netip.AddrPortFrom(testServerIP, 0))
	if err != nil {
		return errors.New("self-test TCP listener failed")
	}
	udpListener, err := server.net.ListenUDPAddrPort(netip.AddrPortFrom(testServerIP, 0))
	if err != nil {
		tcpListener.Close()
		return errors.New("self-test UDP listener failed")
	}
	dnsListener, err := server.net.ListenUDPAddrPort(netip.AddrPortFrom(testServerIP, 53))
	if err != nil {
		tcpListener.Close()
		udpListener.Close()
		return errors.New("self-test DNS listener failed")
	}
	var workers sync.WaitGroup
	tcpResult := make(chan error, 1)
	udpResult := make(chan error, 1)
	dnsResult := make(chan error, 1)
	deadline, _ := ctx.Deadline()
	workers.Add(3)
	go func() { defer workers.Done(); tcpResult <- serveTestTCP(tcpListener, deadline) }()
	go func() { defer workers.Done(); udpResult <- serveTestUDP(udpListener, deadline) }()
	go func() { defer workers.Done(); dnsResult <- serveTestDNS(dnsListener, deadline) }()
	defer func() { client.Close(); tcpListener.Close(); udpListener.Close(); dnsListener.Close(); workers.Wait() }()
	tcpAddress := net.JoinHostPort(testServerIP.String(), strconv.Itoa(tcpListener.Addr().(*net.TCPAddr).Port))
	udpAddress := net.JoinHostPort(testServerIP.String(), strconv.Itoa(udpListener.LocalAddr().(*net.UDPAddr).Port))
	if err := testTCP(ctx, client, "tcp4", tcpAddress, deadline); err != nil {
		return errors.New("self-test TCP failed")
	}
	if err := testUDP(ctx, client, "udp4", udpAddress, deadline); err != nil {
		return errors.New("self-test UDP failed")
	}
	nameAddress := net.JoinHostPort(testName, strconv.Itoa(tcpListener.Addr().(*net.TCPAddr).Port))
	if err := testTCP(ctx, client, "tcp4", nameAddress, deadline); err != nil {
		return errors.New("self-test DNS dial failed")
	}
	for _, result := range []<-chan error{tcpResult, udpResult, dnsResult} {
		select {
		case err := <-result:
			if err != nil {
				return errors.New("self-test peer failed")
			}
		case <-ctx.Done():
			return errors.New("self-test timed out")
		}
	}
	if err := client.Close(); err != nil {
		return errors.New("self-test shutdown failed")
	}
	if c, err := client.DialContext(ctx, "tcp4", tcpAddress); err != ErrClosed {
		if c != nil {
			c.Close()
		}
		return errors.New("self-test late dial accepted")
	}
	if err := server.Close(); err != nil {
		return errors.New("self-test shutdown failed")
	}
	return nil
}

// selfTestIPv6 uses fresh keys and the same loopback-only outer bind as IPv4.
// All IPv6 listeners and addresses exist solely inside the userspace stacks.
func selfTestIPv6(parent context.Context) error {
	if parent == nil {
		return errors.New("nil context")
	}
	ctx, cancel := context.WithTimeout(parent, 20*time.Second)
	defer cancel()
	if ctx.Err() != nil {
		return errors.New("self-test cancelled")
	}
	serverKey, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		return errors.New("self-test key generation failed")
	}
	clientKey, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		return errors.New("self-test key generation failed")
	}
	encode := func(b []byte) string { return base64.StdEncoding.EncodeToString(b) }
	server, err := newEngine(Config{PrivateKey: encode(serverKey.Bytes()), PeerPublicKey: encode(clientKey.PublicKey().Bytes()), Endpoint: netip.MustParseAddrPort("127.0.0.1:1"), Addresses: []netip.Addr{testServerIP, testServerIP6}, Routes: []netip.Prefix{netip.MustParsePrefix("10.70.0.2/32"), netip.MustParsePrefix("fd70::2/128")}, MTU: 1280}, &loopbackBind{})
	if err != nil {
		return errors.New("self-test IPv6 server failed")
	}
	defer server.Close()
	client, err := newEngine(Config{PrivateKey: encode(clientKey.Bytes()), PeerPublicKey: encode(serverKey.PublicKey().Bytes()), Endpoint: netip.AddrPortFrom(netip.MustParseAddr("127.0.0.1"), server.ListenPort()), Addresses: []netip.Addr{testClientIP, testClientIP6}, Routes: []netip.Prefix{netip.MustParsePrefix("10.70.0.1/32"), netip.MustParsePrefix("fd70::1/128")}, DNS: []netip.Addr{testServerIP6}, MTU: 1280}, &loopbackBind{})
	if err != nil {
		return errors.New("self-test IPv6 client failed")
	}
	defer client.Close()
	tcpListener, err := server.net.ListenTCPAddrPort(netip.AddrPortFrom(testServerIP6, 0))
	if err != nil {
		return errors.New("self-test IPv6 TCP listener failed")
	}
	defer tcpListener.Close()
	udpListener, err := server.net.ListenUDPAddrPort(netip.AddrPortFrom(testServerIP6, 0))
	if err != nil {
		return errors.New("self-test IPv6 UDP listener failed")
	}
	defer udpListener.Close()
	dnsListener, err := server.net.ListenUDPAddrPort(netip.AddrPortFrom(testServerIP6, 53))
	if err != nil {
		return errors.New("self-test IPv6 DNS listener failed")
	}
	defer dnsListener.Close()
	deadline, _ := ctx.Deadline()
	var workers sync.WaitGroup
	tcpResult, udpResult, dnsResult := make(chan error, 1), make(chan error, 1), make(chan error, 1)
	workers.Add(3)
	go func() { defer workers.Done(); tcpResult <- serveTestTCP(tcpListener, deadline) }()
	go func() { defer workers.Done(); udpResult <- serveTestUDP(udpListener, deadline) }()
	go func() { defer workers.Done(); dnsResult <- serveTestDNS6(dnsListener, deadline) }()
	defer func() { client.Close(); tcpListener.Close(); udpListener.Close(); dnsListener.Close(); workers.Wait() }()
	tcpAddress := net.JoinHostPort(testServerIP6.String(), strconv.Itoa(tcpListener.Addr().(*net.TCPAddr).Port))
	udpAddress := net.JoinHostPort(testServerIP6.String(), strconv.Itoa(udpListener.LocalAddr().(*net.UDPAddr).Port))
	if err := testTCP(ctx, client, "tcp6", tcpAddress, deadline); err != nil {
		return errors.New("self-test IPv6 TCP failed")
	}
	if err := testUDP(ctx, client, "udp6", udpAddress, deadline); err != nil {
		return errors.New("self-test IPv6 UDP failed")
	}
	nameAddress := net.JoinHostPort(testName, strconv.Itoa(tcpListener.Addr().(*net.TCPAddr).Port))
	if err := testTCP(ctx, client, "tcp6", nameAddress, deadline); err != nil {
		return errors.New("self-test IPv6 DNS dial failed")
	}
	wrongFamilyName := net.JoinHostPort(testV6OnlyName, strconv.Itoa(tcpListener.Addr().(*net.TCPAddr).Port))
	if c, err := client.DialContext(ctx, "tcp4", wrongFamilyName); err == nil || err.Error() != "no routed address in requested family" {
		if c != nil {
			c.Close()
		}
		return errors.New("self-test DNS family fallback accepted")
	}
	for _, result := range []<-chan error{tcpResult, udpResult, dnsResult} {
		select {
		case err := <-result:
			if err != nil {
				return errors.New("self-test IPv6 peer failed")
			}
		case <-ctx.Done():
			return errors.New("self-test IPv6 timed out")
		}
	}
	for _, tc := range []struct{ network, address string }{{"tcp4", tcpAddress}, {"udp4", udpAddress}, {"tcp6", "10.70.0.1:80"}, {"udp6", "[fe80::1%en0]:53"}, {"tcp6", "[::ffff:10.70.0.1]:80"}, {"tcp6", "[fd71::1]:80"}} {
		if c, err := client.DialContext(ctx, tc.network, tc.address); err == nil || err.Error() != "unsupported dial address" {
			if c != nil {
				c.Close()
			}
			return errors.New("self-test IPv6 family gate failed")
		}
	}
	querySeen := make(chan struct{}, 1)
	workers.Add(2)
	go func() {
		defer workers.Done()
		b := make([]byte, 512)
		if n, _, err := dnsListener.ReadFrom(b); err == nil && n > 0 {
			querySeen <- struct{}{}
		}
	}()
	pendingDone := make(chan error, 1)
	go func() {
		defer workers.Done()
		c, err := client.DialContext(ctx, "tcp6", "unanswered.invalid:80")
		if c != nil {
			c.Close()
		}
		pendingDone <- err
	}()
	select {
	case <-querySeen:
	case <-ctx.Done():
		return errors.New("self-test IPv6 pending DNS missing")
	}
	if err := client.Close(); err != nil {
		return errors.New("self-test IPv6 shutdown failed")
	}
	select {
	case err := <-pendingDone:
		if err == nil {
			return errors.New("self-test IPv6 pending dial escaped")
		}
	case <-ctx.Done():
		return errors.New("self-test IPv6 pending dial did not join")
	}
	if c, err := client.DialContext(ctx, "tcp6", tcpAddress); err != ErrClosed {
		if c != nil {
			c.Close()
		}
		return errors.New("self-test IPv6 late dial accepted")
	}
	return server.Close()
}

func serveTestDNS6(c net.PacketConn, deadline time.Time) error {
	c.SetDeadline(deadline)
	b := make([]byte, 512)
	seen := map[string]map[dnsmessage.Type]bool{testName + ".": {}, testV6OnlyName + ".": {}}
	for range 4 {
		n, addr, err := c.ReadFrom(b)
		if err != nil {
			return err
		}
		var query dnsmessage.Message
		if err = query.Unpack(b[:n]); err != nil || len(query.Questions) != 1 {
			return errors.New("invalid dual-stack DNS query")
		}
		q := query.Questions[0]
		name := q.Name.String()
		if seen[name] == nil || (q.Type != dnsmessage.TypeA && q.Type != dnsmessage.TypeAAAA) || seen[name][q.Type] {
			return errors.New("unexpected dual-stack DNS query")
		}
		seen[name][q.Type] = true
		header := dnsmessage.ResourceHeader{Name: q.Name, Type: q.Type, Class: dnsmessage.ClassINET, TTL: 10}
		var body dnsmessage.ResourceBody = &dnsmessage.AResource{A: testServerIP.As4()}
		if q.Type == dnsmessage.TypeAAAA {
			body = &dnsmessage.AAAAResource{AAAA: testServerIP6.As16()}
		}
		response := dnsmessage.Message{Header: dnsmessage.Header{ID: query.Header.ID, Response: true, Authoritative: true, RecursionAvailable: true}, Questions: query.Questions}
		if name == testName+"." || q.Type == dnsmessage.TypeAAAA {
			response.Answers = []dnsmessage.Resource{{Header: header, Body: body}}
		}
		packed, err := response.Pack()
		if err != nil {
			return err
		}
		if _, err = c.WriteTo(packed, addr); err != nil {
			return err
		}
	}
	for _, family := range seen {
		if !family[dnsmessage.TypeA] || !family[dnsmessage.TypeAAAA] {
			return errors.New("missing DNS family query")
		}
	}
	return nil
}

func serveTestTCP(listener net.Listener, deadline time.Time) error {
	for i := 0; i < 2; i++ {
		c, err := listener.Accept()
		if err != nil {
			return err
		}
		c.SetDeadline(deadline)
		b := make([]byte, 32)
		n, err := c.Read(b)
		if err == nil {
			_, err = c.Write(b[:n])
		}
		c.Close()
		if err != nil {
			return err
		}
	}
	return nil
}

func serveTestUDP(c net.PacketConn, deadline time.Time) error {
	c.SetDeadline(deadline)
	b := make([]byte, 128)
	for i := 0; i < 2; i++ {
		n, addr, err := c.ReadFrom(b)
		if err != nil {
			return err
		}
		if _, err = c.WriteTo(b[:n], addr); err != nil {
			return err
		}
	}
	return nil
}

func serveTestDNS(c net.PacketConn, deadline time.Time) error {
	c.SetDeadline(deadline)
	b := make([]byte, 512)
	n, addr, err := c.ReadFrom(b)
	if err != nil {
		return err
	}
	var query dnsmessage.Message
	if err = query.Unpack(b[:n]); err != nil || len(query.Questions) != 1 {
		return errors.New("invalid DNS query")
	}
	q := query.Questions[0]
	if q.Name.String() != testName+"." || q.Type != dnsmessage.TypeA {
		return errors.New("unexpected DNS query")
	}
	response := dnsmessage.Message{Header: dnsmessage.Header{ID: query.Header.ID, Response: true, Authoritative: true, RecursionAvailable: true}, Questions: query.Questions, Answers: []dnsmessage.Resource{{Header: dnsmessage.ResourceHeader{Name: q.Name, Type: dnsmessage.TypeA, Class: dnsmessage.ClassINET, TTL: 10}, Body: &dnsmessage.AResource{A: testServerIP.As4()}}}}
	packed, err := response.Pack()
	if err != nil {
		return err
	}
	_, err = c.WriteTo(packed, addr)
	return err
}

func testTCP(ctx context.Context, e *Engine, network, address string, deadline time.Time) error {
	c, err := e.DialContext(ctx, network, address)
	if err != nil {
		return err
	}
	defer c.Close()
	c.SetDeadline(deadline)
	payload := []byte("brisa-tcp-test")
	if _, err = c.Write(payload); err != nil {
		return err
	}
	got := make([]byte, len(payload))
	if _, err = io.ReadFull(c, got); err != nil {
		return err
	}
	if !bytes.Equal(got, payload) {
		return errors.New("TCP mismatch")
	}
	return nil
}

func testUDP(ctx context.Context, e *Engine, network, address string, deadline time.Time) error {
	c, err := e.DialContext(ctx, network, address)
	if err != nil {
		return err
	}
	defer c.Close()
	c.SetDeadline(deadline)
	for _, payload := range [][]byte{[]byte("short"), []byte("longer-datagram")} {
		if _, err = c.Write(payload); err != nil {
			return err
		}
		got := make([]byte, 128)
		n, err := c.Read(got)
		if err != nil {
			return err
		}
		if n != len(payload) || !bytes.Equal(got[:n], payload) {
			return errors.New("UDP mismatch")
		}
	}
	return nil
}
