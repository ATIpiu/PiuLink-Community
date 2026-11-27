package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/signal"
	"time"

	"github.com/atipiu/piulink/packages/tunnel"
	"github.com/atipiu/piulink/packages/tunnel/updates"
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

type event struct {
	Type    string        `json:"type"`
	Message string        `json:"message,omitempty"`
	Peer    string        `json:"peer,omitempty"`
	Stats   *tunnel.Stats `json:"stats,omitempty"`
}

func main() {
	if err := run(); err != nil {
		writeEvent(event{Type: "error", Message: err.Error()})
		os.Exit(1)
	}
}

func run() error {
	if len(os.Args) > 1 && os.Args[1] == "update" {
		args := os.Args[2:]
		if len(args) == 3 && args[0] == "check" {
			r, e := updates.Check(args[1], args[2])
			if e != nil {
				return e
			}
			return json.NewEncoder(os.Stdout).Encode(r)
		}
		if len(args) == 5 && args[0] == "prepare" {
			r, e := updates.Prepare(args[1], args[2], args[3], args[4])
			if e != nil {
				return e
			}
			return json.NewEncoder(os.Stdout).Encode(r)
		}
		return errors.New("usage: update check CHANNEL VERSION | update prepare CHANNEL VERSION CACHE BASE")
	}
	decoder := json.NewDecoder(io.LimitReader(os.Stdin, 64<<10))
	decoder.DisallowUnknownFields()
	var input inputConfig
	if err := decoder.Decode(&input); err != nil {
		return fmt.Errorf("read tunnel configuration from stdin: %w", err)
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	// The parent owns stdin. Closing it also tears down pending ICE checks.
	go func() { _, _ = io.Copy(io.Discard, os.Stdin); stop() }()
	session, err := tunnel.Start(ctx, tunnel.Config{
		Lease: input.Lease, ClientID: input.ClientID, Role: input.Role,
		ListenHost: input.ListenHost, ForwardHost: input.ForwardHost,
		TCPPorts: input.TCPPorts, UDPPorts: input.UDPPorts,
	})
	if err != nil {
		return err
	}
	defer session.Close()
	writeEvent(event{Type: "ready", Peer: session.PeerAddress()})

	wait := make(chan error, 1)
	go func() { wait <- session.Wait() }()
	ticker := time.NewTicker(10 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return nil
		case err := <-wait:
			if err == nil || errors.Is(err, context.Canceled) {
				return nil
			}
			return err
		case <-ticker.C:
			stats := session.Stats()
			writeEvent(event{Type: "stats", Stats: &stats})
		}
	}
}

func writeEvent(value event) {
	payload, err := json.Marshal(value)
	if err == nil {
		_, _ = os.Stdout.Write(append(payload, '\n'))
	}
}
