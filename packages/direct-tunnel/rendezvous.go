package tunnel

import (
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/stun/v4"
)

type description struct {
	User        string   `json:"user"`
	Password    string   `json:"password"`
	Candidates  []string `json:"candidates"`
	Fingerprint string   `json:"fingerprint"`
	Proof       string   `json:"proof"`
}

func (d description) proof(key []byte, id string, role Role) string {
	d.Proof = ""
	body, _ := json.Marshal(d)
	return encodeBase64URL(hmacSHA256(key, append([]byte("piulink-ice-v2|"+id+"|"+string(role)+"|"), body...)))
}
func hmacSHA256(key, value []byte) []byte {
	h := hmac.New(sha256.New, key)
	h.Write(value)
	return h.Sum(nil)
}

type iceSocket struct {
	*ice.Conn
	peer     *net.UDPAddr
	media    chan []byte
	overflow atomic.Uint64
}

// Video bursts must not compete for quic-go's 32-slot non-QUIC queue.
func (s *iceSocket) ReadFrom(p []byte) (int, net.Addr, error) {
	for {
		n, err := s.Read(p)
		if err != nil || n == 0 || p[0] != rawMagic {
			return n, s.peer, err
		}
		packet := append([]byte(nil), p[:n]...)
		select {
		case s.media <- packet:
		default:
			s.overflow.Add(1)
		}
	}
}
func (s *iceSocket) WriteTo(p []byte, a net.Addr) (int, error) {
	if a.String() != s.peer.String() {
		return 0, errors.New("unexpected ICE peer")
	}
	return s.Write(p)
}

type rendezvousResult struct {
	socket              *iceSocket
	peer                *net.UDPAddr
	hostCertFingerprint string
	transportKey        []byte
}

func physical(name string) bool {
	name = strings.ToLower(name)
	// WSL2 guests connect directly to their Windows Host via its Hyper-V gateway.
	if strings.HasPrefix(name, "vethernet (wsl") {
		return true
	}
	for _, prefix := range []string{"tailscale", "tun", "utun", "meta", "clash", "vethernet", "veth", "docker", "lo", "wg", "dummy", "ipsec"} {
		if strings.HasPrefix(name, prefix) {
			return false
		}
	}
	return true
}
func usableIP(ip net.IP) bool {
	return ip.IsGlobalUnicast() && !ip.IsLoopback() && !ip.IsLinkLocalUnicast() &&
		!(ip.To4() != nil && (ip.To4()[0] == 0 || ip.To4()[0] == 198 && (ip.To4()[1] == 18 || ip.To4()[1] == 19)))
}
func rendezvous(ctx context.Context, cfg Config, cert *ephemeralCertificate) (result *rendezvousResult, err error) {
	base, err := ServiceBase(cfg.Lease.ServiceBase)
	if err != nil {
		return nil, err
	}
	u, _ := url.Parse(base)
	host := u.Hostname()
	if base == DefaultService {
		host = "172.245.182.117"
	}
	stunURL, err := stun.ParseURI("stun:" + net.JoinHostPort(host, "3478"))
	if err != nil {
		return nil, err
	}
	return rendezvousOn(ctx, cfg, cert, directICEConfig(stunURL), base+"v2/exchange")
}

func directICEConfig(stunURL *stun.URI) *ice.AgentConfig {
	gatherTimeout := 2100 * time.Millisecond
	return &ice.AgentConfig{Urls: []*stun.URI{stunURL}, NetworkTypes: []ice.NetworkType{ice.NetworkTypeUDP4, ice.NetworkTypeUDP6},
		CandidateTypes: []ice.CandidateType{ice.CandidateTypeHost, ice.CandidateTypeServerReflexive}, InterfaceFilter: physical, IPFilter: usableIP,
		RemoteIPFilter: usableIP, MulticastDNSMode: ice.MulticastDNSModeDisabled, STUNGatherTimeout: &gatherTimeout}
}

func rendezvousOn(ctx context.Context, cfg Config, cert *ephemeralCertificate, network *ice.AgentConfig, endpoint string) (result *rendezvousResult, err error) {
	agent, err := ice.NewAgent(network)
	if err != nil {
		return nil, err
	}
	defer func() {
		if err != nil {
			agent.Close()
		}
	}()
	done := make(chan struct{})
	if err = agent.OnCandidate(func(c ice.Candidate) {
		if c == nil {
			close(done)
		}
	}); err != nil {
		return nil, err
	}
	if err = agent.GatherCandidates(); err != nil {
		return nil, err
	}
	select {
	case <-done:
	case <-ctx.Done():
		return nil, ctx.Err()
	}
	local, _ := agent.GetLocalCandidates()
	desc := description{}
	desc.User, desc.Password, err = agent.GetLocalUserCredentials()
	if err != nil {
		return nil, err
	}
	for _, c := range local {
		if (c.Type() == ice.CandidateTypeHost || c.Type() == ice.CandidateTypeServerReflexive) && len(desc.Candidates) < 24 {
			desc.Candidates = append(desc.Candidates, c.Marshal())
		}
	}
	if len(desc.Candidates) == 0 {
		return nil, errors.New("no direct UDP candidates on physical interfaces")
	}
	if cert != nil {
		desc.Fingerprint = cert.fingerprint
	}
	key, _ := decodeBase64URL(cfg.Lease.TransportKey)
	desc.Proof = desc.proof(key, cfg.Lease.LeaseID, cfg.Role)
	remote, err := exchange(ctx, cfg.Lease, desc, endpoint)
	if err != nil {
		return nil, err
	}
	peerRole := RoleHost
	if cfg.Role == RoleHost {
		peerRole = RoleSource
	}
	if !hmac.Equal([]byte(remote.Proof), []byte(remote.proof(key, cfg.Lease.LeaseID, peerRole))) {
		return nil, errors.New("peer description authentication failed")
	}
	for _, line := range remote.Candidates {
		c, e := ice.UnmarshalCandidate(line)
		if e != nil || c.NetworkType().IsTCP() || !usableIP(net.ParseIP(c.Address())) || (c.Type() != ice.CandidateTypeHost && c.Type() != ice.CandidateTypeServerReflexive) {
			continue
		}
		if err = agent.AddRemoteCandidate(c); err != nil {
			return nil, err
		}
	}
	var conn *ice.Conn
	if cfg.Role == RoleSource {
		conn, err = agent.Dial(ctx, remote.User, remote.Password)
	} else {
		conn, err = agent.Accept(ctx, remote.User, remote.Password)
	}
	if err != nil {
		return nil, fmt.Errorf("direct ICE checks failed (%d local, %d remote candidates): %w", len(desc.Candidates), len(remote.Candidates), err)
	}
	peer, ok := conn.RemoteAddr().(*net.UDPAddr)
	if !ok || peer == nil {
		return nil, errors.New("missing selected UDP pair")
	}
	fp := remote.Fingerprint
	if cert != nil {
		fp = cert.fingerprint
	}
	return &rendezvousResult{&iceSocket{Conn: conn, peer: peer, media: make(chan []byte, 2048)}, peer, fp, key}, nil
}

var signalClient = &http.Client{Timeout: 5 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}

func exchange(ctx context.Context, l Lease, desc description, endpoint string) (description, error) {
	data, _ := json.Marshal(map[string]any{"id": l.LeaseID, "info": desc})
	for {
		remote, err := exchangeOnce(ctx, endpoint, l.Ticket, data)
		if remote != nil {
			return *remote, nil
		}
		if err != nil {
			var networkError net.Error
			if !(errors.As(err, &networkError) && networkError.Timeout()) &&
				!errors.Is(err, io.EOF) && !errors.Is(err, io.ErrUnexpectedEOF) &&
				!errors.Is(err, syscall.ECONNRESET) && !errors.Is(err, syscall.EPIPE) {
				return description{}, fmt.Errorf("ICE signaling unavailable: %w", err)
			}
		}
		select {
		case <-ctx.Done():
			return description{}, fmt.Errorf("ICE signaling deadline: %w", ctx.Err())
		case <-time.After(350 * time.Millisecond):
		}
	}
}

func exchangeOnce(ctx context.Context, endpoint, ticket string, data []byte) (*description, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(data))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+ticket)
	req.Header.Set("Content-Type", "application/json")
	resp, err := signalClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 8193))
	if err != nil {
		return nil, err
	}
	switch resp.StatusCode {
	case http.StatusNoContent, http.StatusBadGateway, http.StatusServiceUnavailable, http.StatusGatewayTimeout:
		return nil, nil
	case http.StatusOK:
		if len(body) > 8192 {
			return nil, errors.New("ICE description too large")
		}
		var remote description
		if err := json.Unmarshal(body, &remote); err != nil {
			return nil, err
		}
		return &remote, nil
	default:
		return nil, fmt.Errorf("ICE signaling returned HTTP %d", resp.StatusCode)
	}
}
