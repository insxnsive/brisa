package macengine

import (
	"context"
	"crypto/ecdh"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"net"
	"net/netip"
	"strings"
	"sync"
	"testing"
	"time"
)

func testConfig(t *testing.T) Config {
	t.Helper()
	a, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	b, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return Config{PrivateKey: base64.StdEncoding.EncodeToString(a.Bytes()), PeerPublicKey: base64.StdEncoding.EncodeToString(b.PublicKey().Bytes()), Endpoint: netip.MustParseAddrPort("127.0.0.1:12345"), Address: netip.MustParseAddr("10.70.0.2"), AllowedIP: netip.MustParsePrefix("10.70.0.0/24"), MTU: 1280, DNS: []netip.Addr{netip.MustParseAddr("10.70.0.1")}}
}

func TestInvalidConfigHasNoSideEffects(t *testing.T) {
	base := testConfig(t)
	cases := map[string]func(*Config){
		"key":              func(c *Config) { c.PrivateKey = "invalid-secret" },
		"zero key":         func(c *Config) { c.PrivateKey = base64.StdEncoding.EncodeToString(make([]byte, 32)) },
		"peer key":         func(c *Config) { c.PeerPublicKey = "invalid-secret" },
		"noncanonical key": func(c *Config) { c.PrivateKey = base.PrivateKey[:43] + "A" },
		"endpoint":         func(c *Config) { c.Endpoint = netip.AddrPort{} },
		"ipv6 endpoint":    func(c *Config) { c.Endpoint = netip.MustParseAddrPort("[::1]:12345") },
		"endpoint port":    func(c *Config) { c.Endpoint = netip.MustParseAddrPort("127.0.0.1:0") },
		"mtu":              func(c *Config) { c.MTU = 1000 },
		"address":          func(c *Config) { c.Address = netip.Addr{} },
		"dns":              func(c *Config) { c.DNS = []netip.Addr{{}} },
		"ipv6 dns":         func(c *Config) { c.DNS = []netip.Addr{netip.IPv6Loopback()} },
	}
	for name, change := range cases {
		t.Run(name, func(t *testing.T) {
			c := base
			change(&c)
			e, err := New(c)
			if err == nil {
				if e != nil {
					e.Close()
				}
				t.Fatal("accepted invalid config")
			}
			if e != nil {
				t.Fatal("engine returned")
			}
			if name == "key" && strings.Contains(err.Error(), "invalid-secret") {
				t.Fatal("secret leaked")
			}
		})
	}
}

func TestCancelledContext(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	c := testConfig(t)
	e, err := newEngine(c, &loopbackBind{})
	if err != nil {
		t.Fatal(err)
	}
	defer e.Close()
	if conn, err := e.DialContext(ctx, "tcp4", "10.70.0.1:80"); err == nil {
		conn.Close()
		t.Fatal("cancelled dial succeeded")
	}
}

func TestCloseCancelsPendingDNSAndJoins(t *testing.T) {
	c := testConfig(t)
	e, err := newEngine(c, &loopbackBind{})
	if err != nil {
		t.Fatal(err)
	}
	dialDone := make(chan error, 1)
	go func() {
		conn, err := e.DialContext(context.Background(), "tcp4", "missing.invalid:80")
		if conn != nil {
			conn.Close()
		}
		dialDone <- err
	}()
	deadline := time.After(2 * time.Second)
	for {
		e.mu.Lock()
		n := e.pendingCount
		e.mu.Unlock()
		if n > 0 {
			break
		}
		select {
		case <-deadline:
			e.Close()
			t.Fatal("dial did not begin")
		default:
			time.Sleep(time.Millisecond)
		}
	}
	closeDone := make(chan struct{})
	go func() { e.Close(); close(closeDone) }()
	select {
	case <-closeDone:
	case <-time.After(time.Second):
		t.Fatal("close did not cancel pending DNS")
	}
	select {
	case err := <-dialDone:
		if err == nil {
			t.Fatal("late dial succeeded")
		}
	case <-time.After(time.Second):
		t.Fatal("dial did not join")
	}
	again := make(chan struct{})
	go func() { e.Close(); close(again) }()
	select {
	case <-again:
	case <-time.After(time.Second):
		t.Fatal("repeated close hung")
	}
}

func TestUnsupportedDialCannotUseHostResolver(t *testing.T) {
	c := testConfig(t)
	c.DNS = nil
	e, err := newEngine(c, &loopbackBind{})
	if err != nil {
		t.Fatal(err)
	}
	defer e.Close()
	for _, tc := range []struct{ network, address string }{{"tcp6", "[::1]:80"}, {"udp6", "[::1]:53"}, {"tcp4", "[::1]:80"}, {"tcp4", "localhost:80"}, {"icmp", "10.70.0.1:1"}} {
		if conn, err := e.DialContext(context.Background(), tc.network, tc.address); err == nil {
			conn.Close()
			t.Fatalf("accepted %s %s", tc.network, tc.address)
		}
	}
}

func TestCloseClosesActiveConnection(t *testing.T) {
	a, _ := ecdh.X25519().GenerateKey(rand.Reader)
	b, _ := ecdh.X25519().GenerateKey(rand.Reader)
	enc := func(v []byte) string { return base64.StdEncoding.EncodeToString(v) }
	server, err := newEngine(Config{PrivateKey: enc(a.Bytes()), PeerPublicKey: enc(b.PublicKey().Bytes()), Endpoint: netip.MustParseAddrPort("127.0.0.1:1"), Address: testServerIP, AllowedIP: netip.MustParsePrefix("10.70.0.2/32"), MTU: 1280}, &loopbackBind{})
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	client, err := newEngine(Config{PrivateKey: enc(b.Bytes()), PeerPublicKey: enc(a.PublicKey().Bytes()), Endpoint: netip.AddrPortFrom(netip.MustParseAddr("127.0.0.1"), server.ListenPort()), Address: testClientIP, AllowedIP: netip.MustParsePrefix("10.70.0.1/32"), MTU: 1280}, &loopbackBind{})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	listener, err := server.net.ListenTCPAddrPort(netip.AddrPortFrom(testServerIP, 0))
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	connection, err := client.DialContext(ctx, "tcp4", listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	accepted, err := listener.Accept()
	if err != nil {
		t.Fatal(err)
	}
	defer accepted.Close()
	readDone := make(chan error, 1)
	go func() { b := make([]byte, 1); _, err := connection.Read(b); readDone <- err }()
	closed := make(chan struct{})
	go func() { client.Close(); close(closed) }()
	select {
	case <-closed:
	case <-ctx.Done():
		t.Fatal("close hung")
	}
	select {
	case err := <-readDone:
		if err == nil {
			t.Fatal("read succeeded after close")
		}
	case <-ctx.Done():
		t.Fatal("read stayed blocked")
	}
	if _, err := client.DialContext(ctx, "tcp4", listener.Addr().String()); err != ErrClosed {
		t.Fatal("late dial accepted")
	}
}

func TestCloseInterruptsDNSAfterEncryptedRequest(t *testing.T) {
	a, _ := ecdh.X25519().GenerateKey(rand.Reader)
	b, _ := ecdh.X25519().GenerateKey(rand.Reader)
	enc := func(v []byte) string { return base64.StdEncoding.EncodeToString(v) }
	server, err := newEngine(Config{PrivateKey: enc(a.Bytes()), PeerPublicKey: enc(b.PublicKey().Bytes()), Endpoint: netip.MustParseAddrPort("127.0.0.1:1"), Address: testServerIP, AllowedIP: netip.MustParsePrefix("10.70.0.2/32"), MTU: 1280}, &loopbackBind{})
	if err != nil {
		t.Fatal(err)
	}
	defer server.Close()
	client, err := newEngine(Config{PrivateKey: enc(b.Bytes()), PeerPublicKey: enc(a.PublicKey().Bytes()), Endpoint: netip.AddrPortFrom(netip.MustParseAddr("127.0.0.1"), server.ListenPort()), Address: testClientIP, AllowedIP: netip.MustParsePrefix("10.70.0.1/32"), DNS: []netip.Addr{testServerIP}, MTU: 1280}, &loopbackBind{})
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	dns, err := server.net.ListenUDPAddrPort(netip.AddrPortFrom(testServerIP, 53))
	if err != nil {
		t.Fatal(err)
	}
	defer dns.Close()
	requestSeen := make(chan struct{})
	readDone := make(chan struct{})
	go func() {
		defer close(readDone)
		buf := make([]byte, 512)
		if n, _, err := dns.ReadFrom(buf); err == nil && n > 0 {
			close(requestSeen)
		}
	}()
	dialDone := make(chan error, 1)
	go func() {
		conn, err := client.DialContext(context.Background(), "tcp4", "unanswered.invalid:80")
		if conn != nil {
			conn.Close()
		}
		dialDone <- err
	}()
	select {
	case <-requestSeen:
	case <-time.After(3 * time.Second):
		t.Fatal("DNS query did not traverse tunnel")
	}
	closed := make(chan struct{}, 2)
	for i := 0; i < 2; i++ {
		go func() { client.Close(); closed <- struct{}{} }()
	}
	for i := 0; i < 2; i++ {
		select {
		case <-closed:
		case <-time.After(time.Second):
			t.Fatal("concurrent close hung after DNS request")
		}
	}
	select {
	case err := <-dialDone:
		if err == nil {
			t.Fatal("dial succeeded without DNS answer")
		}
	case <-time.After(time.Second):
		t.Fatal("pending dial did not join")
	}
	dns.Close()
	select {
	case <-readDone:
	case <-time.After(time.Second):
		t.Fatal("DNS reader hung")
	}
}

type blockingConn struct {
	started, release, closed chan struct{}
	once                     sync.Once
}

func (c *blockingConn) Read([]byte) (int, error) {
	close(c.started)
	<-c.release
	return 0, net.ErrClosed
}
func (c *blockingConn) Write(b []byte) (int, error)      { return len(b), nil }
func (c *blockingConn) Close() error                     { c.once.Do(func() { close(c.closed) }); return nil }
func (c *blockingConn) LocalAddr() net.Addr              { return &net.TCPAddr{} }
func (c *blockingConn) RemoteAddr() net.Addr             { return &net.TCPAddr{} }
func (c *blockingConn) SetDeadline(time.Time) error      { return nil }
func (c *blockingConn) SetReadDeadline(time.Time) error  { return nil }
func (c *blockingConn) SetWriteDeadline(time.Time) error { return nil }

func TestCloseJoinsActiveRead(t *testing.T) {
	e, err := newEngine(testConfig(t), &loopbackBind{})
	if err != nil {
		t.Fatal(err)
	}
	fake := &blockingConn{started: make(chan struct{}), release: make(chan struct{}), closed: make(chan struct{})}
	owned := &ownedConn{Conn: fake, owner: e}
	e.mu.Lock()
	e.active[owned] = struct{}{}
	e.mu.Unlock()
	readDone := make(chan error, 1)
	go func() { _, err := owned.Read(make([]byte, 1)); readDone <- err }()
	select {
	case <-fake.started:
	case <-time.After(time.Second):
		t.Fatal("read did not start")
	}
	closeDone := make(chan struct{})
	go func() { e.Close(); close(closeDone) }()
	select {
	case <-fake.closed:
	case <-time.After(time.Second):
		t.Fatal("connection was not closed")
	}
	select {
	case <-closeDone:
		close(fake.release)
		t.Fatal("close returned before read joined")
	case <-time.After(50 * time.Millisecond):
	}
	close(fake.release)
	select {
	case err := <-readDone:
		if !errors.Is(err, net.ErrClosed) {
			t.Fatal("unexpected read result")
		}
	case <-time.After(time.Second):
		t.Fatal("read did not return")
	}
	select {
	case <-closeDone:
	case <-time.After(time.Second):
		t.Fatal("close did not join read")
	}
}
