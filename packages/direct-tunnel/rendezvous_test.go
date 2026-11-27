package tunnel

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"
	"time"

	"github.com/pion/logging"
	"github.com/pion/stun/v4"
	"github.com/pion/transport/v5/vnet"
)

func TestExchangeRetriesSameDescription(t *testing.T) {
	for _, mode := range []string{"gateway-unavailable", "lost-response"} {
		t.Run(mode, func(t *testing.T) { testSignalRetry(t, mode) })
	}
}

func testSignalRetry(t *testing.T, mode string) {
	var attempts int
	var mu sync.Mutex
	var first []byte
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		mu.Lock()
		defer mu.Unlock()
		attempts++
		body, err := io.ReadAll(r.Body)
		if err != nil {
			t.Error(err)
			return
		}
		if attempts == 1 {
			first = body
			if mode == "lost-response" {
				conn, _, err := w.(http.Hijacker).Hijack()
				if err != nil {
					t.Error(err)
					return
				}
				conn.Close()
				return
			}
			w.WriteHeader(http.StatusServiceUnavailable)
			return
		}
		if !bytes.Equal(first, body) {
			t.Error("retry changed candidate description")
		}
		json.NewEncoder(w).Encode(description{User: "peer"})
	}))
	defer server.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	peer, err := exchange(ctx, Lease{LeaseID: "test", Ticket: "source"}, description{User: "source"}, server.URL)
	mu.Lock()
	defer mu.Unlock()
	if err != nil || peer.User != "peer" || attempts != 2 {
		t.Fatalf("attempts=%d, peer=%s, err=%v", attempts, peer.User, err)
	}
}

// Two separate private subnets. Each NAT accepts incoming UDP only after its
// own endpoint has sent to that exact remote IP and port.
func TestPunchAcrossPortRestrictedNATs(t *testing.T) {
	testNATPair(t, vnet.EndpointIndependent, true, 0)
}
func TestPhysicalPunchAfterSlowVPNSignaling(t *testing.T) {
	testNATPair(t, vnet.EndpointIndependent, true, 10*time.Second)
}
func TestTwoSymmetricNATsFailWithoutRelay(t *testing.T) {
	testNATPair(t, vnet.EndpointAddrPortDependent, false, 0)
}
func testNATPair(t *testing.T, mapping vnet.EndpointDependencyType, reachable bool, signalingDelay time.Duration) {
	must := func(err error) {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
	}
	logger := logging.NewDefaultLoggerFactory()
	wan, err := vnet.NewRouter(&vnet.RouterConfig{CIDR: "0.0.0.0/0", LoggerFactory: logger})
	must(err)
	newLAN := func(cidr, local, external string) *vnet.Net {
		t.Helper()
		nat := &vnet.NATType{MappingBehavior: mapping, FilteringBehavior: vnet.EndpointAddrPortDependent}
		router, e := vnet.NewRouter(&vnet.RouterConfig{CIDR: cidr, StaticIPs: []string{external}, NATType: nat, LoggerFactory: logger})
		must(e)
		nic, e := vnet.NewNet(&vnet.NetConfig{StaticIPs: []string{local}})
		must(e)
		must(router.AddNet(nic))
		must(wan.AddRouter(router))
		return nic
	}
	sourceNet := newLAN("10.1.0.0/24", "10.1.0.10", "203.0.113.21")
	hostNet := newLAN("192.168.3.0/24", "192.168.3.20", "203.0.113.72")
	stunNet, err := vnet.NewNet(&vnet.NetConfig{StaticIPs: []string{"198.51.100.9"}})
	must(err)
	must(wan.AddNet(stunNet))
	must(wan.Start())
	defer wan.Stop()
	conn, err := stunNet.ListenPacket("udp4", "198.51.100.9:3478")
	must(err)
	defer conn.Close()
	go func() {
		buf := make([]byte, 1024)
		for {
			n, peer, e := conn.ReadFrom(buf)
			if e != nil {
				return
			}
			req := &stun.Message{Raw: buf[:n]}
			if req.Decode() != nil || req.Type != stun.BindingRequest {
				continue
			}
			udp := peer.(*net.UDPAddr)
			reply, e := stun.Build(stun.NewTransactionIDSetter(req.TransactionID), stun.BindingSuccess, &stun.XORMappedAddress{IP: udp.IP, Port: udp.Port})
			if e == nil {
				conn.WriteTo(reply.Raw, peer)
			}
		}
	}()

	var mu sync.Mutex
	descriptions := map[string]description{}
	offerReady := time.Now().Add(signalingDelay)
	signaling := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var msg struct{ Info description }
		if json.NewDecoder(r.Body).Decode(&msg) != nil {
			w.WriteHeader(400)
			return
		}
		side := r.Header.Get("Authorization")
		other := "Bearer host"
		if side == other {
			other = "Bearer source"
		}
		mu.Lock()
		defer mu.Unlock()
		descriptions[side] = msg.Info
		if peer, ok := descriptions[other]; ok && !time.Now().Before(offerReady) {
			json.NewEncoder(w).Encode(peer)
		} else {
			w.WriteHeader(204)
		}
	}))
	defer signaling.Close()
	secret := make([]byte, 32)
	_, err = rand.Read(secret)
	must(err)
	cert, err := createEphemeralCertificate(time.Now())
	must(err)
	uri, err := stun.ParseURI("stun:198.51.100.9:3478")
	must(err)
	deadline := 8 * time.Second
	if signalingDelay > 0 {
		deadline += signalingDelay
	}
	if !reachable {
		deadline = 3 * time.Second
	}
	ctx, cancel := context.WithTimeout(context.Background(), deadline)
	defer cancel()
	type outcome struct {
		path *rendezvousResult
		err  error
	}
	run := func(role Role, nic *vnet.Net, identity *ephemeralCertificate, done chan<- outcome) {
		network := directICEConfig(uri)
		network.Net = nic
		cfg := Config{Role: role, ClientID: string(role), Lease: Lease{LeaseID: "test", Ticket: string(role),
			SourceClientID: string(RoleSource), HostClientID: string(RoleHost), TransportKey: encodeBase64URL(secret)}}
		if signalingDelay > 0 && role == RoleSource {
			cfg.ICENetwork, cfg.ICEInterface = nic, "eth0"
		}
		if err := cfg.normalize(); err != nil {
			done <- outcome{nil, err}
			return
		}
		startup, stop := context.WithTimeout(ctx, cfg.StartupTimeout)
		defer stop()
		path, e := rendezvousOn(startup, cfg, identity, network, signaling.URL)
		done <- outcome{path, e}
	}
	src, host := make(chan outcome, 1), make(chan outcome, 1)
	go run(RoleHost, hostNet, cert, host)
	go run(RoleSource, sourceNet, nil, src)
	a, b := <-src, <-host
	if a.path != nil {
		defer a.path.socket.Close()
	}
	if b.path != nil {
		defer b.path.socket.Close()
	}
	if !reachable {
		if a.err == nil || b.err == nil || len(descriptions) != 2 {
			t.Fatal("expected connectivity failure after successful exchange")
		}
		return
	}
	must(a.err)
	must(b.err)
	if a.path.peer.IP.String() != "203.0.113.72" || b.path.peer.IP.String() != "203.0.113.21" {
		t.Fatal("expected mapped UDP endpoints")
	}
	signaling.Close()
	conn.Close()
	for i, pair := range [][2]*iceSocket{{a.path.socket, b.path.socket}, {b.path.socket, a.path.socket}} {
		data := bytes.Repeat([]byte{0x40, byte(i + 1)}, 512)
		_, e := pair[0].Write(data)
		must(e)
		must(pair[1].SetReadDeadline(time.Now().Add(time.Second)))
		buf := make([]byte, 2048)
		n, _, e := pair[1].ReadFrom(buf)
		must(e)
		if !bytes.Equal(data, buf[:n]) {
			t.Fatal("peer datagram differs")
		}
	}
}
