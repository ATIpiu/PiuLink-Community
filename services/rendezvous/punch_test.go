package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"net"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestOpeningRetryKeepsOriginalRoleTicket(t *testing.T) {
	now := time.Now()
	room, other := strings.Repeat("a", 64), strings.Repeat("b", 64)
	until := now.Add(time.Minute)
	state := punchState{calls: map[string]*call{}, hosts: map[string]hostPoll{room: {until: until}, other: {until: until}}}
	d := directory{now: func() time.Time { return now }, rooms: map[string]entry{room: {until: until}, other: {until: until}}}
	server := httptest.NewTLSServer(nil)
	defer server.Close()
	o := offer{ID: strings.Repeat("d", 32), Time: now.Unix(), Cert: base64.StdEncoding.EncodeToString(server.Certificate().Raw), Signature: "signed", Wrapped: "wrapped"}
	send := func(key string, o offer, want int) string {
		t.Helper()
		body, _ := json.Marshal(o)
		req := httptest.NewRequest("POST", prefix+"v2/open", bytes.NewReader(body))
		w := httptest.NewRecorder()
		state.serve(w, req, key, &d)
		if w.Code != want {
			t.Fatalf("status %d, want %d", w.Code, want)
		}
		return w.Body.String()
	}
	first := send(room, o, 200)
	if second := send(room, o, 200); second != first || len(state.calls) != 1 {
		t.Fatal("identical opening request allocated twice")
	}
	send(other, o, 409)
	o.Signature = "different"
	send(room, o, 409)
}

func TestHostRecoversMissingPollReply(t *testing.T) {
	now := time.Now()
	owner := strings.Repeat("a", 64)
	hostID := "00000000-0000-4000-8000-000000000001"
	room := roomKey(owner, hostID)
	server := httptest.NewTLSServer(nil)
	defer server.Close()
	pending := &call{room: room, offer: offer{ID: strings.Repeat("1", 32)}, expires: now.Add(time.Minute), tickets: [2]string{"source", "host"}}
	state := punchState{calls: map[string]*call{pending.offer.ID: pending}, hosts: map[string]hostPoll{}}
	d := directory{now: func() time.Time { return now }, rooms: map[string]entry{room: {until: now.Add(time.Minute)}}}
	body, _ := json.Marshal(map[string]string{"hostId": hostID, "cert": base64.StdEncoding.EncodeToString(server.Certificate().Raw)})
	poll := func(want int) string {
		t.Helper()
		req := httptest.NewRequest("POST", prefix+"v2/host", bytes.NewReader(body))
		w := httptest.NewRecorder()
		state.serve(w, req, owner, &d)
		if w.Code != want {
			t.Fatalf("poll status %d, want %d", w.Code, want)
		}
		return w.Body.String()
	}
	first := poll(200)
	poll(204)
	now = now.Add(5 * time.Second)
	if retry := poll(200); retry != first {
		t.Fatal("redelivery changed the signed offer or ticket")
	}
	pending.ice[1] = &iceInfo{}
	now = now.Add(5 * time.Second)
	poll(204)
}

func TestDirectOnlyExchange(t *testing.T) {
	now := time.Now()
	id := strings.Repeat("1", 32)
	call := &call{expires: now.Add(time.Minute), tickets: [2]string{strings.Repeat("a", 64), strings.Repeat("b", 64)}}
	punches.calls[id] = call
	defer delete(punches.calls, id)
	d := &directory{now: func() time.Time { return now }}
	source := iceInfo{User: "abcde", Password: strings.Repeat("x", 24), Proof: strings.Repeat("p", 43),
		Candidates: []string{"123456 1 udp 2130706431 192.168.10.15 55000 typ host ufrag abcde generation 0"}}
	host := source
	host.Fingerprint = strings.Repeat("f", 64)
	host.Candidates = []string{"456 1 udp 100 203.0.113.40 50000 typ srflx raddr 192.168.5.1 rport 50000 ufrag abcde generation 0"}
	send := func(key string, info iceInfo, want int) string {
		t.Helper()
		b, _ := json.Marshal(map[string]any{"id": id, "info": info})
		r := httptest.NewRequest("POST", prefix+"v2/exchange", bytes.NewReader(b))
		r.Header.Set("Authorization", "Bearer "+key)
		w := httptest.NewRecorder()
		d.ServeHTTP(w, r)
		if w.Code != want {
			t.Fatalf("got %d want %d", w.Code, want)
		}
		return w.Body.String()
	}
	send(strings.Repeat("c", 64), source, 403)
	relay := source
	relay.Candidates = []string{"123 1 udp 1 203.0.113.40 1 typ relay"}
	send(call.tickets[0], relay, 400)
	send(call.tickets[0], source, 204)
	overwritten := source
	overwritten.Password = "replacement"
	send(call.tickets[0], overwritten, 204)
	if !strings.Contains(send(call.tickets[1], host, 200), source.Password) {
		t.Fatal("source description changed")
	}
	if !strings.Contains(send(call.tickets[0], source, 200), host.Fingerprint) {
		t.Fatal("missing authenticated host description")
	}
	now = now.Add(time.Minute)
	send(call.tickets[0], source, 404)
}

func TestSTUNBindingOnly(t *testing.T) {
	s, e := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if e != nil {
		t.Fatal(e)
	}
	defer s.Close()
	go serveSTUN(s)
	c, e := net.DialUDP("udp4", nil, s.LocalAddr().(*net.UDPAddr))
	if e != nil {
		t.Fatal(e)
	}
	defer c.Close()
	req := []byte{0, 1, 0, 0, 0x21, 0x12, 0xa4, 0x42, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12}
	c.SetDeadline(time.Now().Add(time.Second))
	c.Write(req)
	var out [40]byte
	n, e := c.Read(out[:])
	if e != nil || n != 32 || out[0] != 1 || out[1] != 1 || !bytes.Equal(out[8:20], req[8:20]) || out[21] != 0x20 {
		t.Fatal("invalid Binding response", e)
	}
	req[1] = 3 // TURN Allocate
	c.Write(req)
	c.SetReadDeadline(time.Now().Add(80 * time.Millisecond))
	if _, e = c.Read(out[:]); e == nil {
		t.Fatal("TURN request received a response")
	}
}
