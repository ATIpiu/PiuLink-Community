package tunnel

import (
	"encoding/base64"
	"errors"
	"fmt"
	"net"
	"net/url"
	"strings"
	"time"
)

const (
	RoleSource Role = "source"
	RoleHost   Role = "host"
)

var (
	DefaultTCPPorts = []uint16{47984, 47989, 48010, 48011}
	DefaultUDPPorts = []uint16{47998, 47999, 48000, 48010}
)

type Role string

// Lease is the authenticated HTTPS response used to establish one short-lived
// direct path. TransportKey must never be logged or persisted.
type Lease struct {
	ServiceBase    string    `json:"serviceBase,omitempty"`
	LeaseID        string    `json:"leaseId"`
	Ticket         string    `json:"ticket"`
	TransportKey   string    `json:"transportKey"`
	SourceClientID string    `json:"sourceClientId"`
	HostClientID   string    `json:"hostClientId"`
	ExpiresAt      time.Time `json:"expiresAt"`
}

const DefaultService = "https://atipiu.org:8443/piulink/"

func ServiceBase(value string) (string, error) {
	if value == "" {
		return DefaultService, nil
	}
	u, err := url.Parse(value)
	if err != nil || u.Scheme != "https" || u.Hostname() == "" || u.User != nil || u.EscapedPath() != "/piulink/" || strings.ContainsAny(value, "\\#?") {
		return "", errors.New("invalid HTTPS invitation service")
	}
	return u.String(), nil
}

type Config struct {
	Lease          Lease
	ClientID       string
	Role           Role
	ListenHost     string
	ForwardHost    string
	TCPPorts       []uint16
	UDPPorts       []uint16
	StartupTimeout time.Duration
	IdleTimeout    time.Duration
}

func (c *Config) normalize() error {
	if c.ListenHost == "" {
		c.ListenHost = "127.0.0.1"
	}
	if c.ForwardHost == "" {
		c.ForwardHost = "127.0.0.1"
	}
	if len(c.TCPPorts) == 0 {
		c.TCPPorts = append([]uint16(nil), DefaultTCPPorts...)
	}
	if len(c.UDPPorts) == 0 {
		c.UDPPorts = append([]uint16(nil), DefaultUDPPorts...)
	}
	if c.StartupTimeout <= 0 {
		c.StartupTimeout = 24 * time.Second
	}
	if c.IdleTimeout <= 0 {
		c.IdleTimeout = 45 * time.Second
	}
	if c.Role != RoleSource && c.Role != RoleHost {
		return errors.New("role must be source or host")
	}
	if c.Lease.LeaseID == "" || c.Lease.Ticket == "" || c.ClientID == "" {
		return errors.New("lease ID, ticket, and client ID are required")
	}
	if !c.Lease.ExpiresAt.IsZero() && !c.Lease.ExpiresAt.After(time.Now()) {
		return errors.New("connection lease has expired")
	}
	expectedID := c.Lease.SourceClientID
	if c.Role == RoleHost {
		expectedID = c.Lease.HostClientID
	}
	if c.ClientID != expectedID {
		return errors.New("client ID does not match the lease role")
	}
	key, err := decodeBase64URL(c.Lease.TransportKey)
	if err != nil || len(key) != 32 {
		return errors.New("transport key must be a 32-byte base64url value")
	}
	if !net.ParseIP(c.ListenHost).IsLoopback() || !net.ParseIP(c.ForwardHost).IsLoopback() {
		return errors.New("endpoint adapters must bind to loopback")
	}
	if err := validatePorts(c.TCPPorts); err != nil {
		return fmt.Errorf("TCP ports: %w", err)
	}
	if err := validatePorts(c.UDPPorts); err != nil {
		return fmt.Errorf("UDP ports: %w", err)
	}
	return nil
}

func (c Config) peerClientID() string {
	if c.Role == RoleSource {
		return c.Lease.HostClientID
	}
	return c.Lease.SourceClientID
}

func validatePorts(ports []uint16) error {
	seen := make(map[uint16]struct{}, len(ports))
	for _, port := range ports {
		if port == 0 {
			return errors.New("port 0 is not allowed")
		}
		if _, exists := seen[port]; exists {
			return fmt.Errorf("duplicate port %d", port)
		}
		seen[port] = struct{}{}
	}
	return nil
}

func decodeBase64URL(value string) ([]byte, error) {
	return base64.RawURLEncoding.DecodeString(value)
}

func encodeBase64URL(value []byte) string {
	return base64.RawURLEncoding.EncodeToString(value)
}
