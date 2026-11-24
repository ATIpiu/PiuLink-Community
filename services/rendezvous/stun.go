package main

import (
	"bytes"
	"encoding/binary"
	"net"
	"time"
)

// A Binding-only IPv4 STUN responder. No allocations, permissions or TURN channels.
func serveSTUN(conn *net.UDPConn) {
	var buf [1024]byte
	var cookie = []byte{0x21, 0x12, 0xa4, 0x42}
	counts := map[string]int{}
	next := time.Now().Add(time.Second)
	for {
		n, peer, err := conn.ReadFromUDP(buf[:])
		if err != nil {
			return
		}
		if n < 20 || n >= len(buf) || n%4 != 0 || binary.BigEndian.Uint16(buf[:2]) != 1 || int(binary.BigEndian.Uint16(buf[2:4])) != n-20 || !bytes.Equal(buf[4:8], cookie) {
			continue
		}
		now := time.Now()
		if now.After(next) {
			clear(counts)
			next = now.Add(time.Second)
		}
		ip := peer.IP.To4()
		if ip == nil || len(counts) >= 2048 || counts[ip.String()] >= 30 {
			continue
		}
		counts[ip.String()]++
		var out [32]byte
		copy(out[:20], buf[:20])
		binary.BigEndian.PutUint16(out[:2], 0x0101)
		binary.BigEndian.PutUint16(out[2:4], 12)
		binary.BigEndian.PutUint16(out[20:22], 0x0020)
		binary.BigEndian.PutUint16(out[22:24], 8)
		out[25] = 1
		binary.BigEndian.PutUint16(out[26:28], uint16(peer.Port)^0x2112)
		for i := 0; i < 4; i++ {
			out[28+i] = ip[i] ^ cookie[i]
		}
		conn.WriteToUDP(out[:], peer)
	}
}
