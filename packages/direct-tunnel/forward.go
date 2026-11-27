package tunnel

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"

	quic "github.com/quic-go/quic-go"
)

const tcpStreamHeaderSize = 5

type sourceUDPProxy struct {
	connection *net.UDPConn
	clientMu   sync.RWMutex
	client     *net.UDPAddr
}

func (s *Session) startForwarders(ctx context.Context) error {
	if s.cfg.Role == RoleSource {
		if err := s.startSourceTCP(ctx); err != nil {
			return err
		}
		return s.startSourceUDP(ctx)
	}
	s.startHostTCP(ctx)
	return s.startHostUDP(ctx)
}

func (s *Session) startSourceTCP(ctx context.Context) error {
	listeners := make([]net.Listener, 0, len(s.cfg.TCPPorts))
	for _, port := range s.cfg.TCPPorts {
		listener, err := net.Listen("tcp", net.JoinHostPort(s.cfg.ListenHost, fmt.Sprint(port)))
		if err != nil {
			for _, opened := range listeners {
				_ = opened.Close()
			}
			return fmt.Errorf("listen on local TCP port %d: %w", port, err)
		}
		listeners = append(listeners, listener)
		s.addCloser(listener)
		go s.acceptLocalTCP(ctx, listener, port)
	}
	return nil
}

func (s *Session) acceptLocalTCP(ctx context.Context, listener net.Listener, port uint16) {
	for {
		local, err := listener.Accept()
		if err != nil {
			if ctx.Err() == nil && !errors.Is(err, net.ErrClosed) {
				s.fail(fmt.Errorf("accept local TCP port %d: %w", port, err))
			}
			return
		}
		go s.forwardLocalTCP(ctx, local, port)
	}
}

func (s *Session) forwardLocalTCP(ctx context.Context, local net.Conn, port uint16) {
	defer local.Close()
	stream, err := s.connection.OpenStreamSync(ctx)
	if err != nil {
		return
	}
	s.tcpStreams.Add(1)
	header := []byte{'P', 'T', 1, 0, 0}
	binary.BigEndian.PutUint16(header[3:5], port)
	if _, err = stream.Write(header); err != nil {
		stream.CancelRead(streamError)
		stream.CancelWrite(streamError)
		return
	}
	proxyTCP(local, stream)
}

func (s *Session) startHostTCP(ctx context.Context) {
	allowed := make(map[uint16]struct{}, len(s.cfg.TCPPorts))
	for _, port := range s.cfg.TCPPorts {
		allowed[port] = struct{}{}
	}
	go func() {
		for {
			stream, err := s.connection.AcceptStream(ctx)
			if err != nil {
				return
			}
			go s.forwardRemoteTCP(stream, allowed)
		}
	}()
}

func (s *Session) forwardRemoteTCP(stream *quic.Stream, allowed map[uint16]struct{}) {
	header := make([]byte, tcpStreamHeaderSize)
	if _, err := io.ReadFull(stream, header); err != nil ||
		header[0] != 'P' || header[1] != 'T' || header[2] != 1 {
		stream.CancelRead(streamError)
		stream.CancelWrite(streamError)
		return
	}
	port := binary.BigEndian.Uint16(header[3:5])
	if _, ok := allowed[port]; !ok {
		stream.CancelRead(streamError)
		stream.CancelWrite(streamError)
		return
	}
	target, err := net.Dial("tcp", net.JoinHostPort(s.cfg.ForwardHost, fmt.Sprint(port)))
	if err != nil {
		stream.CancelRead(streamError)
		stream.CancelWrite(streamError)
		return
	}
	s.tcpStreams.Add(1)
	defer target.Close()
	proxyTCP(target, stream)
}

func proxyTCP(tcpConnection net.Conn, stream *quic.Stream) {
	done := make(chan struct{}, 2)
	go func() {
		_, _ = io.Copy(stream, tcpConnection)
		_ = stream.Close()
		done <- struct{}{}
	}()
	go func() {
		_, _ = io.Copy(tcpConnection, stream)
		if tcp, ok := tcpConnection.(*net.TCPConn); ok {
			_ = tcp.CloseWrite()
		}
		done <- struct{}{}
	}()
	<-done
	<-done
}

func (s *Session) startSourceUDP(ctx context.Context) error {
	proxies := make(map[uint16]*sourceUDPProxy, len(s.cfg.UDPPorts))
	for _, port := range s.cfg.UDPPorts {
		address, err := net.ResolveUDPAddr("udp", net.JoinHostPort(s.cfg.ListenHost, fmt.Sprint(port)))
		if err != nil {
			return err
		}
		connection, err := net.ListenUDP("udp", address)
		if err != nil {
			return fmt.Errorf("listen on local UDP port %d: %w", port, err)
		}
		_ = connection.SetReadBuffer(4 << 20)
		_ = connection.SetWriteBuffer(4 << 20)
		proxy := &sourceUDPProxy{connection: connection}
		proxies[port] = proxy
		s.addCloser(connection)
		go s.readSourceUDP(ctx, port, proxy)
	}
	s.raw.setHandler(func(port uint16, payload []byte) {
		proxy := proxies[port]
		if proxy == nil {
			return
		}
		proxy.clientMu.RLock()
		client := proxy.client
		proxy.clientMu.RUnlock()
		if client != nil {
			_, _ = proxy.connection.WriteToUDP(payload, client)
		}
	})
	return nil
}

func (s *Session) readSourceUDP(ctx context.Context, port uint16, proxy *sourceUDPProxy) {
	buffer := make([]byte, 65535)
	for {
		n, client, err := proxy.connection.ReadFromUDP(buffer)
		if err != nil {
			return
		}
		proxy.clientMu.Lock()
		proxy.client = client
		proxy.clientMu.Unlock()
		if err := s.raw.send(port, buffer[:n]); err != nil && ctx.Err() != nil {
			return
		}
	}
}

func (s *Session) startHostUDP(ctx context.Context) error {
	connections := make(map[uint16]*net.UDPConn, len(s.cfg.UDPPorts))
	for _, port := range s.cfg.UDPPorts {
		target, err := net.ResolveUDPAddr("udp", net.JoinHostPort(s.cfg.ForwardHost, fmt.Sprint(port)))
		if err != nil {
			return err
		}
		connection, err := net.DialUDP("udp", nil, target)
		if err != nil {
			return fmt.Errorf("open Sunshine UDP forward for port %d: %w", port, err)
		}
		_ = connection.SetReadBuffer(4 << 20)
		_ = connection.SetWriteBuffer(4 << 20)
		connections[port] = connection
		s.addCloser(connection)
		go s.readHostUDP(ctx, port, connection)
	}
	s.raw.setHandler(func(port uint16, payload []byte) {
		if connection := connections[port]; connection != nil {
			_, _ = connection.Write(payload)
		}
	})
	return nil
}

func (s *Session) readHostUDP(ctx context.Context, port uint16, connection *net.UDPConn) {
	buffer := make([]byte, 65535)
	for {
		n, err := connection.Read(buffer)
		if err != nil {
			return
		}
		if err := s.raw.send(port, buffer[:n]); err != nil && ctx.Err() != nil {
			return
		}
	}
}
