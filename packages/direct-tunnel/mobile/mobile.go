// Package mobile exposes the direct tunnel through a gomobile-generated Android AAR.
package mobile

import (
	"context"
	"encoding/json"
	"errors"
	"net"
	"sync"
	"time"

	"github.com/atipiu/piulink/packages/tunnel"
	"github.com/pion/transport/v5"
	"github.com/pion/transport/v5/stdnet"
)

type inputConfig struct {
	Lease       tunnel.Lease `json:"lease"`
	ClientID    string       `json:"clientId"`
	Role        tunnel.Role  `json:"role"`
	ListenHost  string       `json:"listenHost,omitempty"`
	ForwardHost string       `json:"forwardHost,omitempty"`
	TCPPorts    []uint16     `json:"tcpPorts,omitempty"`
	UDPPorts    []uint16     `json:"udpPorts,omitempty"`
}

// Session owns one active direct path and its local Sunshine-compatible proxy ports.
type Session struct {
	mu      sync.Mutex
	session *tunnel.Session
}

// Start blocks until the peer is authenticated and all local proxy ports are ready.
func Start(configJSON string) (*Session, error) {
	return start(configJSON, "", nil)
}

// SocketBinder assigns an unconnected ICE socket to an Android Network.
// The fd is borrowed for the duration of Bind and must not be closed by the callback.
type SocketBinder interface {
	Bind(fd int) error
}

type boundUDPNetwork struct {
	*stdnet.Net
	binder SocketBinder
}

func (n *boundUDPNetwork) ListenUDP(network string, local *net.UDPAddr) (transport.UDPConn, error) {
	conn, err := net.ListenUDP(network, local)
	if err != nil {
		return nil, err
	}
	raw, err := conn.SyscallConn()
	if err == nil {
		var bindErr error
		err = raw.Control(func(fd uintptr) { bindErr = n.binder.Bind(int(fd)) })
		if err == nil {
			err = bindErr
		}
	}
	if err != nil {
		conn.Close()
		return nil, err
	}
	return conn, nil
}

// StartOnNetwork binds only direct ICE sockets. HTTPS signaling keeps its normal route.
func StartOnNetwork(configJSON, iface string, binder SocketBinder) (*Session, error) {
	if iface == "" || binder == nil {
		return nil, errors.New("missing direct network")
	}
	return start(configJSON, iface, binder)
}

func start(configJSON, iface string, binder SocketBinder) (*Session, error) {
	var input inputConfig
	if err := json.Unmarshal([]byte(configJSON), &input); err != nil {
		return nil, errors.New("invalid PiuLink tunnel configuration")
	}
	cfg := tunnel.Config{
		Lease: input.Lease, ClientID: input.ClientID, Role: input.Role,
		ListenHost: input.ListenHost, ForwardHost: input.ForwardHost,
		TCPPorts: input.TCPPorts, UDPPorts: input.UDPPorts,
	}
	if binder != nil {
		n, err := stdnet.NewNet()
		if err != nil {
			return nil, err
		}
		cfg.ICENetwork = &boundUDPNetwork{n, binder}
		cfg.ICEInterface = iface
		cfg.StartupTimeout = 9 * time.Second
	}
	active, err := tunnel.Start(context.Background(), cfg)
	if err != nil {
		return nil, err
	}
	return &Session{session: active}, nil
}

func (s *Session) PeerAddress() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.session == nil {
		return ""
	}
	return s.session.PeerAddress()
}

func (s *Session) StatsJSON() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.session == nil {
		return "{}"
	}
	payload, err := json.Marshal(s.session.Stats())
	if err != nil {
		return "{}"
	}
	return string(payload)
}

func (s *Session) Stop() {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.session != nil {
		_ = s.session.Close()
		s.session = nil
	}
}

func (s *Session) Alive() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.session != nil && s.session.Alive()
}
