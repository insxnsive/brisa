// Package macengine provides a userspace WireGuard transport.
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
	Addresses     []netip.Addr   // typed inner addresses; exclusive with Address
	Routes        []netip.Prefix // typed peer routes; exclusive with AllowedIP
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
	return a.IsValid() && a.Zone() == "" && !a.Is4In6() && !a.IsUnspecified() && !a.IsMulticast() && !a.IsLinkLocalUnicast() && a != netip.MustParseAddr("255.255.255.255")
}

func usableRoute(p netip.Prefix) bool {
	if !p.IsValid() || p != p.Masked() {
		return false
	}
	a := p.Addr()
	return (usableIP(a) || (p.Bits() == 0 && a.IsUnspecified() && a.Zone() == "")) && !a.IsLoopback()
}

func fields(c Config) ([]netip.Addr, []netip.Prefix, error) {
	if len(c.Addresses) > 0 || len(c.Routes) > 0 {
		if c.Address.IsValid() || c.AllowedIP.IsValid() || len(c.Addresses) == 0 || len(c.Routes) == 0 {
			return nil, nil, errors.New("mixed or incomplete tunnel addresses and routes")
		}
		return c.Addresses, c.Routes, nil
	}
	return []netip.Addr{c.Address}, []netip.Prefix{c.AllowedIP}, nil
}

func routed(routes []netip.Prefix, a netip.Addr) bool {
	for _, route := range routes {
		if route.Contains(a) {
			return true
		}
	}
	return false
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
	if !c.Endpoint.IsValid() || !c.Endpoint.Addr().Is4() || !usableIP(c.Endpoint.Addr()) || c.Endpoint.Port() == 0 {
		return "", "", errors.New("invalid peer endpoint")
	}
	addresses, routes, err := fields(c)
	if err != nil {
		return "", "", err
	}
	var families [2]bool
	for _, a := range addresses {
		if !usableIP(a) || a.IsLoopback() {
			return "", "", errors.New("invalid tunnel address")
		}
		index := 0
		if a.Is6() {
			index = 1
		}
		if families[index] {
			return "", "", errors.New("duplicate tunnel family")
		}
		families[index] = true
	}
	var routeFamilies [2]bool
	for _, route := range routes {
		if !usableRoute(route) {
			return "", "", errors.New("invalid peer route")
		}
		index := 0
		if route.Addr().Is6() {
			index = 1
		}
		routeFamilies[index] = true
	}
	if families != routeFamilies {
		return "", "", errors.New("address and route families differ")
	}
	if c.MTU < 1280 || c.MTU > 9000 {
		return "", "", errors.New("invalid tunnel MTU")
	}
	for _, d := range c.DNS {
		if !usableIP(d) || d.IsLoopback() || !routed(routes, d) {
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
	routes       []netip.Prefix
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
	addresses, routes, _ := fields(c)
	tun, tnet, err := netstack.CreateNetTUN(addresses, append([]netip.Addr(nil), c.DNS...), c.MTU)
	if err != nil {
		return nil, errors.New("cannot create userspace tunnel")
	}
	dev := device.NewDevice(tun, bind, device.NewLogger(device.LogLevelSilent, ""))
	uapi := fmt.Sprintf("private_key=%s\nlisten_port=0\npublic_key=%s\nendpoint=%s\n", private, public, c.Endpoint.String())
	for _, route := range routes {
		uapi += "allowed_ip=" + route.String() + "\n"
	}
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
	return &Engine{ctx: ctx, cancel: cancel, done: make(chan struct{}), active: make(map[net.Conn]struct{}), net: tnet, dev: dev, port: uint16(port), dns: append([]netip.Addr(nil), c.DNS...), routes: append([]netip.Prefix(nil), routes...)}, nil
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
	if network != "tcp4" && network != "tcp6" && network != "udp4" && network != "udp6" {
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
		if !usableIP(ip) || !routed(e.routes, ip) || (ip.Is4() != (network[3] == '4')) {
			return nil, errors.New("unsupported dial address")
		}
	} else if strings.ContainsAny(host, ":%") {
		return nil, errors.New("unsupported dial address")
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
	if _, err := netip.ParseAddr(host); err != nil {
		resolved, lookupErr := e.net.LookupContextHost(dialCtx, host)
		if lookupErr != nil {
			if e.ctx.Err() != nil {
				return nil, ErrClosed
			}
			if ctx.Err() != nil {
				return nil, ctx.Err()
			}
			return nil, errors.New("in-tunnel DNS failed")
		}
		found := false
		for _, value := range resolved {
			ip, parseErr := netip.ParseAddr(value)
			if parseErr == nil && usableIP(ip) && ip.Is4() == (network[3] == '4') && routed(e.routes, ip) {
				host = ip.String()
				found = true
				break
			}
		}
		if !found {
			return nil, errors.New("no routed address in requested family")
		}
	}
	raw, err := e.net.DialContext(dialCtx, network, net.JoinHostPort(host, portText))
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
