package mobile

import (
	"bytes"
	"context"
	"crypto"
	"crypto/rand"
	"crypto/rsa"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
	"time"

	"github.com/atipiu/piulink/packages/tunnel"
)

var invitation = regexp.MustCompile(`^([0-9a-f]{64})\.([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$`)
var directory = &http.Client{Timeout: 7 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}

func inviteRequest(ctx context.Context, base, method, room string, body []byte, result any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, base+"v2/"+method, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Authorization", "Bearer "+room)
	if len(body) > 0 {
		req.Header.Set("Content-Type", "application/json")
	}
	res, err := directory.Do(req)
	if err != nil {
		return errors.New("could not contact the invitation service")
	}
	defer res.Body.Close()
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		return fmt.Errorf("invitation service returned HTTP %d", res.StatusCode)
	}
	data, err := io.ReadAll(io.LimitReader(res.Body, 8193))
	if err != nil || len(data) > 8192 {
		return errors.New("invalid invitation response")
	}
	return json.Unmarshal(data, result)
}

// StartInvite uses the streaming client's own pairing identity. pinnedDER is
// the certificate saved by Moonlight when this Host was paired.
func StartInvite(link string, certPEM, keyPEM, pinnedDER []byte) (*Session, error) {
	prefix, fragment, found := strings.Cut(link, "join#")
	base, err := tunnel.ServiceBase(prefix)
	match := invitation.FindStringSubmatch(fragment)
	if !found || prefix == "" || err != nil || match == nil {
		return nil, errors.New("invalid PiuLink invitation")
	}
	identity, err := tls.X509KeyPair(certPEM, keyPEM)
	if err != nil {
		return nil, errors.New("invalid Moonlight identity")
	}
	signer, ok := identity.PrivateKey.(*rsa.PrivateKey)
	if !ok {
		return nil, errors.New("Moonlight identity must use RSA")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	var peer struct{ Cert []byte }
	if err = inviteRequest(ctx, base, "identity", match[1], nil, &peer); err != nil {
		return nil, err
	}
	if len(pinnedDER) > 0 && !bytes.Equal(pinnedDER, peer.Cert) {
		return nil, errors.New("Host certificate differs from the saved pairing")
	}
	cert, err := x509.ParseCertificate(peer.Cert)
	if err != nil {
		return nil, errors.New("invalid Host certificate")
	}
	pub, ok := cert.PublicKey.(*rsa.PublicKey)
	if !ok || pub.N.BitLen() < 2048 {
		return nil, errors.New("invalid Host public key")
	}
	key := make([]byte, 32)
	defer clear(key)
	id := make([]byte, 16)
	if _, err = rand.Read(key); err != nil {
		return nil, err
	}
	if _, err = rand.Read(id); err != nil {
		return nil, err
	}
	wrapped, err := rsa.EncryptOAEP(sha256.New(), rand.Reader, pub, key, nil)
	if err != nil {
		return nil, err
	}
	requestID := hex.EncodeToString(id)
	now := time.Now().Unix()
	hash := sha256.Sum256(fmt.Appendf(nil, "piulink-punch-v2\n%s\n%s\n%d\n%s", match[1], requestID, now, base64.StdEncoding.EncodeToString(wrapped)))
	sig, err := rsa.SignPKCS1v15(rand.Reader, signer, crypto.SHA256, hash[:])
	if err != nil {
		return nil, err
	}
	offer, err := json.Marshal(map[string]any{"id": requestID, "time": now, "wrapped": wrapped, "cert": identity.Certificate[0], "signature": sig})
	if err != nil {
		return nil, err
	}
	var reply struct{ Ticket string }
	if err = inviteRequest(ctx, base, "open", match[1], offer, &reply); err != nil {
		return nil, err
	}
	session, err := tunnel.Start(context.Background(), tunnel.Config{Role: tunnel.RoleSource, ClientID: "source", ListenHost: "127.0.0.1",
		Lease: tunnel.Lease{ServiceBase: base, LeaseID: requestID, Ticket: reply.Ticket, TransportKey: base64.RawURLEncoding.EncodeToString(key), SourceClientID: "source", HostClientID: "host"}})
	if err != nil {
		return nil, err
	}
	return &Session{session: session}, nil
}
