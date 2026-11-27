package tunnel

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"encoding/binary"
	"errors"
	"fmt"
	"sync"
	"sync/atomic"
	"time"
)

const (
	rawMagic             = byte(0x21) // First two bits are 00, so quic-go demultiplexes it as non-QUIC.
	rawVersion           = byte(1)
	rawHeaderSize        = 20
	rawFragmentPlaintext = 1100
	rawMaxFragments      = 60
	reassemblyLifetime   = 2 * time.Second
)

type rawTunnel struct {
	ctx          context.Context
	socket       *iceSocket
	sendAEAD     cipher.AEAD
	recvAEAD     cipher.AEAD
	sendNonce    [4]byte
	recvNonce    [4]byte
	allowed      map[uint16]struct{}
	sequence     atomic.Uint64
	messageID    atomic.Uint32
	replay       replayWindow
	reassemblyMu sync.Mutex
	reassembly   map[fragmentKey]*fragmentSet
	handlerMu    sync.RWMutex
	handler      func(uint16, []byte)
	packetsSent  atomic.Uint64
	packetsRecv  atomic.Uint64
	bytesSent    atomic.Uint64
	bytesRecv    atomic.Uint64
	dropped      atomic.Uint64
	cancel       context.CancelFunc
}

type fragmentKey struct {
	port      uint16
	messageID uint32
}

type fragmentSet struct {
	created time.Time
	parts   [][]byte
	count   int
}

type replayWindow struct {
	highest uint64
	seen    [4096]uint64
}

func newRawTunnel(
	ctx context.Context,
	socket *iceSocket,
	secret []byte,
	leaseID string,
	role Role,
	ports []uint16,
) (*rawTunnel, error) {
	keySourceToHost := deriveKey(secret, []byte(leaseID), []byte("piulink-raw-v1/source-to-host"), 36)
	keyHostToSource := deriveKey(secret, []byte(leaseID), []byte("piulink-raw-v1/host-to-source"), 36)
	sendMaterial, recvMaterial := keySourceToHost, keyHostToSource
	if role == RoleHost {
		sendMaterial, recvMaterial = keyHostToSource, keySourceToHost
	}
	sendBlock, err := aes.NewCipher(sendMaterial[:32])
	if err != nil {
		return nil, err
	}
	recvBlock, err := aes.NewCipher(recvMaterial[:32])
	if err != nil {
		return nil, err
	}
	sendAEAD, err := cipher.NewGCM(sendBlock)
	if err != nil {
		return nil, err
	}
	recvAEAD, err := cipher.NewGCM(recvBlock)
	if err != nil {
		return nil, err
	}
	rawCtx, cancel := context.WithCancel(ctx)
	tunnel := &rawTunnel{
		ctx: rawCtx, socket: socket,
		sendAEAD: sendAEAD, recvAEAD: recvAEAD,
		allowed:    make(map[uint16]struct{}, len(ports)),
		reassembly: make(map[fragmentKey]*fragmentSet),
		cancel:     cancel,
	}
	copy(tunnel.sendNonce[:], sendMaterial[32:36])
	copy(tunnel.recvNonce[:], recvMaterial[32:36])
	for _, port := range ports {
		tunnel.allowed[port] = struct{}{}
	}
	go tunnel.receiveLoop()
	return tunnel, nil
}

func (t *rawTunnel) setHandler(handler func(uint16, []byte)) {
	t.handlerMu.Lock()
	t.handler = handler
	t.handlerMu.Unlock()
}

func (t *rawTunnel) send(port uint16, payload []byte) error {
	if _, ok := t.allowed[port]; !ok {
		return fmt.Errorf("UDP port %d is not allowed", port)
	}
	if len(payload) > 65507 {
		return errors.New("UDP payload is too large")
	}
	fragmentCount := (len(payload) + rawFragmentPlaintext - 1) / rawFragmentPlaintext
	if fragmentCount == 0 {
		fragmentCount = 1
	}
	if fragmentCount > rawMaxFragments {
		return errors.New("UDP payload requires too many fragments")
	}
	messageID := t.messageID.Add(1)
	for index := 0; index < fragmentCount; index++ {
		start := index * rawFragmentPlaintext
		end := start + rawFragmentPlaintext
		if end > len(payload) {
			end = len(payload)
		}
		sequence := t.sequence.Add(1)
		header := make([]byte, rawHeaderSize)
		header[0] = rawMagic
		header[1] = rawVersion
		binary.BigEndian.PutUint16(header[2:4], port)
		binary.BigEndian.PutUint64(header[4:12], sequence)
		binary.BigEndian.PutUint32(header[12:16], messageID)
		binary.BigEndian.PutUint16(header[16:18], uint16(index))
		binary.BigEndian.PutUint16(header[18:20], uint16(fragmentCount))
		nonce := makeNonce(t.sendNonce, sequence)
		packet := t.sendAEAD.Seal(header, nonce[:], payload[start:end], header)
		if _, err := t.socket.Write(packet); err != nil {
			return err
		}
		t.packetsSent.Add(1)
		t.bytesSent.Add(uint64(end - start))
	}
	return nil
}

func (t *rawTunnel) receiveLoop() {
	for {
		var packet []byte
		select {
		case <-t.ctx.Done():
			return
		case packet = <-t.socket.media:
		}
		port, payload, ok := t.openPacket(packet)
		if !ok {
			t.dropped.Add(1)
			continue
		}
		if payload == nil {
			continue
		} // An authenticated fragment is awaiting its siblings.
		t.handlerMu.RLock()
		handler := t.handler
		t.handlerMu.RUnlock()
		if handler == nil {
			t.dropped.Add(1)
			continue
		}
		t.packetsRecv.Add(1)
		t.bytesRecv.Add(uint64(len(payload)))
		handler(port, payload)
	}
}

func (t *rawTunnel) openPacket(packet []byte) (uint16, []byte, bool) {
	if len(packet) < rawHeaderSize+t.recvAEAD.Overhead() ||
		packet[0] != rawMagic || packet[1] != rawVersion {
		return 0, nil, false
	}
	port := binary.BigEndian.Uint16(packet[2:4])
	if _, ok := t.allowed[port]; !ok {
		return 0, nil, false
	}
	sequence := binary.BigEndian.Uint64(packet[4:12])
	messageID := binary.BigEndian.Uint32(packet[12:16])
	fragmentIndex := int(binary.BigEndian.Uint16(packet[16:18]))
	fragmentCount := int(binary.BigEndian.Uint16(packet[18:20]))
	if fragmentCount < 1 || fragmentCount > rawMaxFragments || fragmentIndex >= fragmentCount {
		return 0, nil, false
	}
	nonce := makeNonce(t.recvNonce, sequence)
	plaintext, err := t.recvAEAD.Open(nil, nonce[:], packet[rawHeaderSize:], packet[:rawHeaderSize])
	if err != nil {
		return 0, nil, false
	}
	if !t.replay.accept(sequence) {
		return 0, nil, false
	}
	if fragmentCount == 1 {
		return port, plaintext, true
	}
	return t.addFragment(port, messageID, fragmentIndex, fragmentCount, plaintext)
}

func (t *rawTunnel) addFragment(
	port uint16,
	messageID uint32,
	index, count int,
	payload []byte,
) (uint16, []byte, bool) {
	t.reassemblyMu.Lock()
	defer t.reassemblyMu.Unlock()
	now := time.Now()
	if len(t.reassembly) > 256 {
		for key, set := range t.reassembly {
			if now.Sub(set.created) > reassemblyLifetime {
				delete(t.reassembly, key)
			}
		}
		if len(t.reassembly) > 256 {
			return 0, nil, false
		}
	}
	key := fragmentKey{port: port, messageID: messageID}
	set := t.reassembly[key]
	if set == nil {
		set = &fragmentSet{created: now, parts: make([][]byte, count)}
		t.reassembly[key] = set
	}
	if len(set.parts) != count || now.Sub(set.created) > reassemblyLifetime {
		delete(t.reassembly, key)
		return 0, nil, false
	}
	if set.parts[index] == nil {
		set.parts[index] = append([]byte(nil), payload...)
		set.count++
	}
	if set.count != count {
		return port, nil, true
	}
	total := 0
	for _, part := range set.parts {
		total += len(part)
	}
	joined := make([]byte, 0, total)
	for _, part := range set.parts {
		joined = append(joined, part...)
	}
	delete(t.reassembly, key)
	return port, joined, true
}

func (w *replayWindow) accept(sequence uint64) bool {
	if sequence == 0 {
		return false
	}
	if sequence <= w.highest && w.highest-sequence >= uint64(len(w.seen)) {
		return false
	}
	slot := sequence % uint64(len(w.seen))
	if w.seen[slot] == sequence {
		return false
	}
	w.seen[slot] = sequence
	if sequence > w.highest {
		w.highest = sequence
	}
	return true
}

func (t *rawTunnel) stats() Stats {
	return Stats{
		UDPPacketsSent:   t.packetsSent.Load(),
		UDPPacketsRecv:   t.packetsRecv.Load(),
		UDPBytesSent:     t.bytesSent.Load(),
		UDPBytesRecv:     t.bytesRecv.Load(),
		DroppedDatagrams: t.dropped.Load() + t.socket.overflow.Load(),
	}
}

func (t *rawTunnel) close() {
	if t != nil && t.cancel != nil {
		t.cancel()
	}
}

func makeNonce(prefix [4]byte, sequence uint64) [12]byte {
	var nonce [12]byte
	copy(nonce[:4], prefix[:])
	binary.BigEndian.PutUint64(nonce[4:], sequence)
	return nonce
}

func deriveKey(secret, salt, info []byte, length int) []byte {
	// RFC 5869 HKDF-SHA256 extract and expand.
	prk := hmacSHA256(salt, secret)
	result := make([]byte, 0, length)
	var previous []byte
	for counter := byte(1); len(result) < length; counter++ {
		input := make([]byte, 0, len(previous)+len(info)+1)
		input = append(input, previous...)
		input = append(input, info...)
		input = append(input, counter)
		previous = hmacSHA256(prk, input)
		result = append(result, previous...)
	}
	return result[:length]
}
