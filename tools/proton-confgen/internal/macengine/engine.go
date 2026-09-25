// Package macengine provides an IPv4-only userspace WireGuard transport.
// It does not create a host interface, route, or resolver.
package macengine

import (
	"context"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"strconv"
	"strings"
	"sync"
	"time"

	"golang.zx2c4.com/wireguard/conn"
	"golang.zx2c4.com/wireguard/device"
	"golang.zx2c4.com/wireguard/tun/netstack"
)

var ErrClosed = errors.New("transport closed")

// Config accepts only typed routing and resolver values; arbitrary UAPI is never accepted.
type Config struct {
	PrivateKey    string         // canonical base64 encoding of 32 bytes
	PeerPublicKey string         // canonical base64 encoding of 32 bytes
	Endpoint      netip.AddrPort // numeric outer peer endpoint
	Address       netip.Addr     // inner IPv4 address
	AllowedIP     netip.Prefix   // inner IPv4 routes through this peer
	DNS           []netip.Addr   // explicit inner resolvers; empty disables names
	MTU           int
}

func decodeKey(s string) (string, error) {
	b, err := base64.StdEncoding.Strict().DecodeString(s)
	if err != nil || len(b) != 32 || base64.StdEncoding.EncodeToString(b) != s {
		return "", errors.New("invalid WireGuard key")
	}
	var nonzero bool
	for _, v := range b {
		nonzero = nonzero || v != 0
	}
	if !nonzero {
		return "", errors.New("invalid WireGuard key")
	}
	return hex.EncodeToString(b), nil
}

func usableIP(a netip.Addr) bool {
	return a.Is4() && !a.IsUnspecified() && !a.IsMulticast() && !a.IsLinkLocalUnicast() && a != netip.MustParseAddr("255.255.255.255")
}

func validate(c Config) (string, string, error) {
	private, err := decodeKey(c.PrivateKey)
	if err != nil {
		return "", "", err
	}
	public, err := decodeKey(c.PeerPublicKey)
	if err != nil {
		return "", "", err
	}
	if !c.Endpoint.IsValid() || !usableIP(c.Endpoint.Addr()) || c.Endpoint.Port() == 0 {
		return "", "", errors.New("invalid peer endpoint")
	}
	if !usableIP(c.Address) || c.Address.IsLoopback() {
		return "", "", errors.New("invalid tunnel address")
	}
	if !c.AllowedIP.IsValid() || !c.AllowedIP.Addr().Is4() || c.AllowedIP.Bits() < 0 || c.AllowedIP.Bits() > 32 || c.AllowedIP != c.AllowedIP.Masked() {
		return "", "", errors.New("invalid peer route")
	}
	if c.MTU < 1280 || c.MTU > 9000 {
		return "", "", errors.New("invalid tunnel MTU")
	}
	for _, d := range c.DNS {
		if !usableIP(d) || d.IsLoopback() {
			return "", "", errors.New("invalid tunnel DNS")
		}
	}
	return private, public, nil
}

type Engine struct {
	mu           sync.Mutex
	closing      bool
	done         chan struct{}
	ctx          context.Context
	cancel       context.CancelFunc
	pending      sync.WaitGroup
	pendingCount int
	operations   sync.WaitGroup
	active       map[net.Conn]struct{}
	net          *netstack.Net
	dev          *device.Device
	port         uint16
	dns          []netip.Addr
}

// New validates all caller input before creating the userspace device or UDP bind.
func New(c Config) (*Engine, error) {
	if _, _, err := validate(c); err != nil {
		return nil, err
	}
	return newEngine(c, conn.NewDefaultBind())
}

func newEngine(c Config, bind conn.Bind) (*Engine, error) {
	private, public, err := validate(c)
	if err != nil {
		return nil, err
	}
	tun, tnet, err := netstack.CreateNetTUN([]netip.Addr{c.Address}, append([]netip.Addr(nil), c.DNS...), c.MTU)
	if err != nil {
		return nil, errors.New("cannot create userspace tunnel")
	}
	dev := device.NewDevice(tun, bind, device.NewLogger(device.LogLevelSilent, ""))
	uapi := fmt.Sprintf("private_key=%s\nlisten_port=0\npublic_key=%s\nallowed_ip=%s\nendpoint=%s\n", private, public, c.AllowedIP.String(), c.Endpoint.String())
	if err := dev.IpcSet(uapi); err != nil {
		dev.Close()
		return nil, errors.New("cannot configure userspace tunnel")
	}
	if err := dev.Up(); err != nil {
		dev.Close()
		return nil, errors.New("cannot start userspace tunnel")
	}
	state, err := dev.IpcGet()
	if err != nil {
		dev.Close()
		return nil, errors.New("cannot read tunnel listener")
	}
	var port uint64
	for _, line := range strings.Split(state, "\n") {
		if strings.HasPrefix(line, "listen_port=") {
			port, _ = strconv.ParseUint(strings.TrimPrefix(line, "listen_port="), 10, 16)
		}
	}
	if port == 0 {
		dev.Close()
		return nil, errors.New("no tunnel listener")
	}
	ctx, cancel := context.WithCancel(context.Background())
	return &Engine{ctx: ctx, cancel: cancel, done: make(chan struct{}), active: make(map[net.Conn]struct{}), net: tnet, dev: dev, port: uint16(port), dns: append([]netip.Addr(nil), c.DNS...)}, nil
}

// ListenPort is the OS UDP port bound by WireGuard; the inner stack stays userspace-only.
func (e *Engine) ListenPort() uint16 { return e.port }

func (e *Engine) DialContext(ctx context.Context, network, address string) (net.Conn, error) {
	if ctx == nil {
		return nil, errors.New("nil context")
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if network != "tcp" && network != "tcp4" && network != "udp" && network != "udp4" {
		return nil, errors.New("unsupported transport protocol")
	}
	host, portText, err := net.SplitHostPort(address)
	if err != nil {
		return nil, errors.New("invalid dial address")
	}
	p, err := strconv.ParseUint(portText, 10, 16)
	if err != nil || p == 0 {
		return nil, errors.New("invalid dial port")
	}
	if ip, err := netip.ParseAddr(host); err == nil {
		if !usableIP(ip) {
			return nil, errors.New("unsupported dial address")
		}
	} else if len(e.netDNS()) == 0 {
		return nil, errors.New("DNS is not configured")
	}
	e.mu.Lock()
	if e.closing {
		e.mu.Unlock()
		return nil, ErrClosed
	}
	e.pending.Add(1)
	e.pendingCount++
	e.mu.Unlock()
	defer func() { e.mu.Lock(); e.pendingCount--; e.mu.Unlock(); e.pending.Done() }()
	dialCtx, cancel := context.WithCancel(ctx)
	stop := context.AfterFunc(e.ctx, cancel)
	defer func() { stop(); cancel() }()
	proto := "tcp4"
	if network == "udp" || network == "udp4" {
		proto = "udp4"
	}
	raw, err := e.net.DialContext(dialCtx, proto, address)
	if err != nil {
		if e.ctx.Err() != nil {
			return nil, ErrClosed
		}
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		return nil, errors.New("userspace dial failed")
	}
	e.mu.Lock()
	if e.closing || ctx.Err() != nil {
		e.mu.Unlock()
		raw.Close()
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		return nil, ErrClosed
	}
	owned := &ownedConn{Conn: raw, owner: e}
	e.active[owned] = struct{}{}
	e.mu.Unlock()
	return owned, nil
}

func (e *Engine) netDNS() []netip.Addr { return e.dns }

type ownedConn struct {
	net.Conn
	owner *Engine
	once  sync.Once
}

func (c *ownedConn) beginOperation() error {
	c.owner.mu.Lock()
	defer c.owner.mu.Unlock()
	if c.owner.closing {
		return ErrClosed
	}
	c.owner.operations.Add(1)
	return nil
}

func (c *ownedConn) Read(b []byte) (int, error) {
	if err := c.beginOperation(); err != nil {
		return 0, err
	}
	defer c.owner.operations.Done()
	return c.Conn.Read(b)
}

func (c *ownedConn) Write(b []byte) (int, error) {
	if err := c.beginOperation(); err != nil {
		return 0, err
	}
	defer c.owner.operations.Done()
	return c.Conn.Write(b)
}

func (c *ownedConn) SetDeadline(t time.Time) error {
	if err := c.beginOperation(); err != nil {
		return err
	}
	defer c.owner.operations.Done()
	return c.Conn.SetDeadline(t)
}

func (c *ownedConn) SetReadDeadline(t time.Time) error {
	if err := c.beginOperation(); err != nil {
		return err
	}
	defer c.owner.operations.Done()
	return c.Conn.SetReadDeadline(t)
}

func (c *ownedConn) SetWriteDeadline(t time.Time) error {
	if err := c.beginOperation(); err != nil {
		return err
	}
	defer c.owner.operations.Done()
	return c.Conn.SetWriteDeadline(t)
}

func (c *ownedConn) Close() error {
	var err error
	c.once.Do(func() { err = c.Conn.Close(); c.owner.mu.Lock(); delete(c.owner.active, c); c.owner.mu.Unlock() })
	return err
}

// Close joins pending dials and closes all returned connections. Concurrent callers share completion.
func (e *Engine) Close() error {
	e.mu.Lock()
	if e.closing {
		done := e.done
		e.mu.Unlock()
		<-done
		return nil
	}
	e.closing = true
	e.cancel()
	conns := make([]net.Conn, 0, len(e.active))
	for c := range e.active {
		conns = append(conns, c)
	}
	e.mu.Unlock()
	for _, c := range conns {
		c.Close()
	}
	e.dev.Close()
	e.pending.Wait()
	e.operations.Wait()
	close(e.done)
	return nil
}
