package macengine

import (
	"errors"
	"net"
	"net/netip"
	"sync"

	"golang.zx2c4.com/wireguard/conn"
)

// loopbackBind is used only by the synthetic diagnostic. Its OS socket is
// explicitly bound to 127.0.0.1 and it refuses every other destination.
type loopbackBind struct {
	mu     sync.Mutex
	socket *net.UDPConn
}
type loopbackEndpoint struct{ addr netip.AddrPort }

func (*loopbackEndpoint) ClearSrc()             {}
func (*loopbackEndpoint) SrcToString() string   { return "" }
func (e *loopbackEndpoint) DstToString() string { return e.addr.String() }
func (e *loopbackEndpoint) DstToBytes() []byte  { b, _ := e.addr.MarshalBinary(); return b }
func (e *loopbackEndpoint) DstIP() netip.Addr   { return e.addr.Addr() }
func (*loopbackEndpoint) SrcIP() netip.Addr     { return netip.Addr{} }

func (b *loopbackBind) Open(port uint16) ([]conn.ReceiveFunc, uint16, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.socket != nil {
		return nil, 0, conn.ErrBindAlreadyOpen
	}
	socket, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1), Port: int(port)})
	if err != nil {
		return nil, 0, err
	}
	b.socket = socket
	receive := func(bufs [][]byte, sizes []int, eps []conn.Endpoint) (int, error) {
		n, addr, err := socket.ReadFromUDPAddrPort(bufs[0])
		if err != nil {
			return 0, err
		}
		sizes[0] = n
		eps[0] = &loopbackEndpoint{addr: addr}
		return 1, nil
	}
	return []conn.ReceiveFunc{receive}, uint16(socket.LocalAddr().(*net.UDPAddr).Port), nil
}
func (b *loopbackBind) Close() error {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.socket == nil {
		return nil
	}
	err := b.socket.Close()
	b.socket = nil
	return err
}
func (*loopbackBind) SetMark(uint32) error { return errors.New("mark unsupported") }
func (*loopbackBind) BatchSize() int       { return 1 }
func (*loopbackBind) ParseEndpoint(s string) (conn.Endpoint, error) {
	a, err := netip.ParseAddrPort(s)
	if err != nil || !a.Addr().IsLoopback() || !a.Addr().Is4() || a.Port() == 0 {
		return nil, errors.New("invalid loopback peer")
	}
	return &loopbackEndpoint{addr: a}, nil
}
func (b *loopbackBind) Send(bufs [][]byte, ep conn.Endpoint) error {
	e, ok := ep.(*loopbackEndpoint)
	if !ok || !e.addr.Addr().Is4() || !e.addr.Addr().IsLoopback() {
		return errors.New("invalid loopback peer")
	}
	b.mu.Lock()
	socket := b.socket
	b.mu.Unlock()
	if socket == nil {
		return net.ErrClosed
	}
	for _, buf := range bufs {
		if _, err := socket.WriteToUDPAddrPort(buf, e.addr); err != nil {
			return err
		}
	}
	return nil
}
