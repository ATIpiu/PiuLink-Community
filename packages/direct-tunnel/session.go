package tunnel

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"
	"sync/atomic"
	"time"

	quic "github.com/quic-go/quic-go"
)

const (
	applicationError quic.ApplicationErrorCode = 0x5049
	streamError      quic.StreamErrorCode      = 0x5049
)

type Stats struct {
	TCPStreams       uint64
	UDPPacketsSent   uint64
	UDPPacketsRecv   uint64
	UDPBytesSent     uint64
	UDPBytesRecv     uint64
	DroppedDatagrams uint64
}

type Session struct {
	cfg        Config
	peer       *net.UDPAddr
	transport  *quic.Transport
	connection *quic.Conn
	raw        *rawTunnel
	cancel     context.CancelFunc
	done       chan struct{}
	errMu      sync.Mutex
	err        error
	closeOnce  sync.Once
	tcpStreams atomic.Uint64
	closersMu  sync.Mutex
	closers    []io.Closer
}

type authMessage struct {
	Version   int    `json:"version"`
	Type      string `json:"type"`
	LeaseID   string `json:"leaseId"`
	ClientID  string `json:"clientId"`
	Nonce     string `json:"nonce"`
	PeerNonce string `json:"peerNonce,omitempty"`
	Proof     string `json:"proof"`
}

func Start(ctx context.Context, cfg Config) (*Session, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	if err := cfg.normalize(); err != nil {
		return nil, err
	}
	startupCtx, startupCancel := context.WithTimeout(ctx, cfg.StartupTimeout)
	defer startupCancel()

	var cert *ephemeralCertificate
	var err error
	if cfg.Role == RoleHost {
		cert, err = createEphemeralCertificate(time.Now())
		if err != nil {
			return nil, fmt.Errorf("create tunnel certificate: %w", err)
		}
	}
	rendezvousPath, err := rendezvous(startupCtx, cfg, cert)
	if err != nil {
		return nil, err
	}
	transport := &quic.Transport{Conn: rendezvousPath.socket, ConnectionIDLength: 8}
	owned := false
	defer func() {
		if !owned {
			rendezvousPath.socket.Close()
		}
	}()
	quicConfig := &quic.Config{
		HandshakeIdleTimeout:  8 * time.Second,
		MaxIdleTimeout:        cfg.IdleTimeout,
		KeepAlivePeriod:       10 * time.Second,
		MaxIncomingStreams:    64,
		MaxIncomingUniStreams: -1,
	}
	var connection *quic.Conn
	if cfg.Role == RoleHost {
		listener, listenErr := transport.Listen(serverTLSConfig(cert), quicConfig)
		if listenErr != nil {
			_ = transport.Close()
			return nil, fmt.Errorf("listen for tunnel: %w", listenErr)
		}
		connection, err = listener.Accept(startupCtx)
		_ = listener.Close()
	} else {
		connection, err = transport.Dial(
			startupCtx,
			rendezvousPath.peer,
			clientTLSConfig(rendezvousPath.hostCertFingerprint),
			quicConfig)
	}
	if err != nil {
		_ = transport.Close()
		return nil, fmt.Errorf("establish QUIC tunnel: %w", err)
	}
	if err = authenticateConnection(
		startupCtx, connection, cfg, rendezvousPath.transportKey,
		rendezvousPath.hostCertFingerprint); err != nil {
		_ = connection.CloseWithError(applicationError, "authentication failed")
		_ = transport.Close()
		return nil, err
	}

	sessionCtx, cancel := context.WithCancel(context.Background())
	session := &Session{
		cfg: cfg, peer: rendezvousPath.peer, transport: transport,
		connection: connection, cancel: cancel, done: make(chan struct{}),
	}
	session.addCloser(rendezvousPath.socket)
	owned = true
	session.raw, err = newRawTunnel(
		sessionCtx, rendezvousPath.socket, rendezvousPath.transportKey,
		cfg.Lease.LeaseID, cfg.Role, cfg.UDPPorts)
	if err != nil {
		session.Close()
		return nil, err
	}
	if err = session.startForwarders(sessionCtx); err != nil {
		session.Close()
		return nil, err
	}
	go session.watchConnection(sessionCtx)
	return session, nil
}

func (s *Session) PeerAddress() string {
	if s == nil || s.peer == nil {
		return ""
	}
	return s.peer.String()
}

func (s *Session) Stats() Stats {
	if s == nil || s.raw == nil {
		return Stats{}
	}
	rawStats := s.raw.stats()
	rawStats.TCPStreams = s.tcpStreams.Load()
	return rawStats
}

func (s *Session) Wait() error {
	if s == nil {
		return nil
	}
	<-s.done
	s.errMu.Lock()
	defer s.errMu.Unlock()
	return s.err
}

func (s *Session) Alive() bool {
	select {
	case <-s.done:
		return false
	default:
		return true
	}
}

func (s *Session) Close() error {
	if s == nil {
		return nil
	}
	s.closeOnce.Do(func() {
		s.cancel()
		s.closersMu.Lock()
		for _, closer := range s.closers {
			_ = closer.Close()
		}
		s.closers = nil
		s.closersMu.Unlock()
		if s.raw != nil {
			s.raw.close()
		}
		if s.connection != nil {
			_ = s.connection.CloseWithError(0, "closed")
		}
		if s.transport != nil {
			_ = s.transport.Close()
		}
		close(s.done)
	})
	return nil
}

func (s *Session) fail(err error) {
	if err != nil && !errors.Is(err, context.Canceled) && !errors.Is(err, net.ErrClosed) {
		s.errMu.Lock()
		if s.err == nil {
			s.err = err
		}
		s.errMu.Unlock()
	}
	_ = s.Close()
}

func (s *Session) addCloser(closer io.Closer) {
	s.closersMu.Lock()
	defer s.closersMu.Unlock()
	s.closers = append(s.closers, closer)
}

func (s *Session) watchConnection(ctx context.Context) {
	select {
	case <-ctx.Done():
		s.fail(nil)
	case <-s.connection.Context().Done():
		s.fail(context.Cause(s.connection.Context()))
	}
}

func authenticateConnection(
	ctx context.Context,
	connection *quic.Conn,
	cfg Config,
	key []byte,
	hostFingerprint string,
) error {
	if cfg.Role == RoleSource {
		stream, err := connection.OpenStreamSync(ctx)
		if err != nil {
			return fmt.Errorf("open authentication stream: %w", err)
		}
		defer stream.Close()
		request := makeAuthMessage(
			"auth", cfg.Lease.LeaseID, cfg.ClientID, "", hostFingerprint, key)
		if err := writeFramedJSON(stream, request); err != nil {
			return err
		}
		var response authMessage
		if err := readFramedJSON(stream, &response); err != nil {
			return err
		}
		if response.Version != 1 || response.Type != "auth_ok" ||
			response.LeaseID != cfg.Lease.LeaseID || response.ClientID != cfg.Lease.HostClientID ||
			response.PeerNonce != request.Nonce ||
			!verifyAuthMessage(response, hostFingerprint, key) {
			return errors.New("host authentication response was invalid")
		}
		return nil
	}

	stream, err := connection.AcceptStream(ctx)
	if err != nil {
		return fmt.Errorf("accept authentication stream: %w", err)
	}
	defer stream.Close()
	var request authMessage
	if err := readFramedJSON(stream, &request); err != nil {
		return err
	}
	if request.Version != 1 || request.Type != "auth" ||
		request.LeaseID != cfg.Lease.LeaseID || request.ClientID != cfg.Lease.SourceClientID ||
		request.PeerNonce != "" || !verifyAuthMessage(request, hostFingerprint, key) {
		return errors.New("source authentication request was invalid")
	}
	response := makeAuthMessage(
		"auth_ok", cfg.Lease.LeaseID, cfg.ClientID, request.Nonce, hostFingerprint, key)
	return writeFramedJSON(stream, response)
}

func makeAuthMessage(
	typeName, leaseID, clientID, peerNonce, hostFingerprint string,
	key []byte,
) authMessage {
	nonceBytes := make([]byte, 16)
	_, _ = rand.Read(nonceBytes)
	message := authMessage{
		Version: 1, Type: typeName, LeaseID: leaseID, ClientID: clientID,
		Nonce: encodeBase64URL(nonceBytes), PeerNonce: peerNonce,
	}
	message.Proof = encodeBase64URL(hmacSHA256(key, authProofInput(message, hostFingerprint)))
	return message
}

func verifyAuthMessage(message authMessage, hostFingerprint string, key []byte) bool {
	actual, err := decodeBase64URL(message.Proof)
	if err != nil {
		return false
	}
	expected := hmacSHA256(key, authProofInput(message, hostFingerprint))
	return subtle.ConstantTimeCompare(actual, expected) == 1
}

func authProofInput(message authMessage, hostFingerprint string) []byte {
	return []byte(fmt.Sprintf(
		"piulink-auth-v1|%s|%s|%s|%s|%s|%s",
		message.Type, message.LeaseID, message.ClientID, message.Nonce,
		message.PeerNonce, hostFingerprint))
}

func writeFramedJSON(writer io.Writer, value any) error {
	payload, err := json.Marshal(value)
	if err != nil {
		return err
	}
	if len(payload) > 4096 {
		return errors.New("control message is too large")
	}
	header := []byte{byte(len(payload) >> 8), byte(len(payload))}
	if _, err = writer.Write(header); err != nil {
		return err
	}
	_, err = writer.Write(payload)
	return err
}

func readFramedJSON(reader io.Reader, value any) error {
	header := make([]byte, 2)
	if _, err := io.ReadFull(reader, header); err != nil {
		return err
	}
	length := int(binary.BigEndian.Uint16(header))
	if length < 2 || length > 4096 {
		return errors.New("invalid control message length")
	}
	payload := make([]byte, length)
	if _, err := io.ReadFull(reader, payload); err != nil {
		return err
	}
	return json.Unmarshal(payload, value)
}
