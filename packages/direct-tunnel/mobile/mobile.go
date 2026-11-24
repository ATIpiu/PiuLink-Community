// Package mobile exposes the direct tunnel through a gomobile-generated Android AAR.
package mobile

import (
	"context"
	"encoding/json"
	"errors"
	"sync"

	"github.com/atipiu/piulink/packages/tunnel"
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
	var input inputConfig
	if err := json.Unmarshal([]byte(configJSON), &input); err != nil {
		return nil, errors.New("invalid PiuLink tunnel configuration")
	}
	active, err := tunnel.Start(context.Background(), tunnel.Config{
		Lease: input.Lease, ClientID: input.ClientID, Role: input.Role,
		ListenHost: input.ListenHost, ForwardHost: input.ForwardHost,
		TCPPorts: input.TCPPorts, UDPPorts: input.UDPPorts,
	})
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
