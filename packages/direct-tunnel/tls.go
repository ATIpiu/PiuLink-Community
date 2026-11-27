package tunnel

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"errors"
	"math/big"
	"time"
)

const tunnelALPN = "piulink-tunnel-v1"

type ephemeralCertificate struct {
	certificate tls.Certificate
	fingerprint string
}

func createEphemeralCertificate(now time.Time) (*ephemeralCertificate, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	serialLimit := new(big.Int).Lsh(big.NewInt(1), 128)
	serial, err := rand.Int(rand.Reader, serialLimit)
	if err != nil {
		return nil, err
	}
	template := x509.Certificate{
		SerialNumber: serial,
		Subject:      pkix.Name{CommonName: "PiuLink ephemeral tunnel"},
		NotBefore:    now.Add(-time.Minute),
		NotAfter:     now.Add(10 * time.Minute),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	der, err := x509.CreateCertificate(rand.Reader, &template, &template, &key.PublicKey, key)
	if err != nil {
		return nil, err
	}
	digest := sha256.Sum256(der)
	return &ephemeralCertificate{
		certificate: tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key},
		fingerprint: hex.EncodeToString(digest[:]),
	}, nil
}

func serverTLSConfig(cert *ephemeralCertificate) *tls.Config {
	return &tls.Config{
		Certificates: []tls.Certificate{cert.certificate},
		MinVersion:   tls.VersionTLS13,
		NextProtos:   []string{tunnelALPN},
	}
}

func clientTLSConfig(expectedFingerprint string) *tls.Config {
	return &tls.Config{
		MinVersion:         tls.VersionTLS13,
		NextProtos:         []string{tunnelALPN},
		InsecureSkipVerify: true, // Verified by the lease-key-authenticated fingerprint below.
		VerifyPeerCertificate: func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
			if len(rawCerts) != 1 {
				return errors.New("PiuLink tunnel returned an invalid certificate chain")
			}
			digest := sha256.Sum256(rawCerts[0])
			if hex.EncodeToString(digest[:]) != expectedFingerprint {
				return errors.New("PiuLink tunnel certificate fingerprint mismatch")
			}
			return nil
		},
	}
}
