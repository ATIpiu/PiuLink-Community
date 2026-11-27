package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestWebInviteRolesAndExpiry(t *testing.T) {
	now := time.Now()
	d := directory{now: func() time.Time { return now }}
	owner := strings.Repeat("a", 64)
	sum := sha256.Sum256([]byte("piulink-web-room\n" + owner))
	room := hex.EncodeToString(sum[:])
	id := strings.Repeat("b", 64)
	desc := webDescription{"v=0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=fingerprint:sha-256 test\r\n", strings.Repeat("f", 64)}
	call := func(endpoint, key string, q webSignal, want int) webSignal {
		t.Helper()
		body, _ := json.Marshal(q)
		r := httptest.NewRequest("POST", prefix+"web/v1/"+endpoint, bytes.NewReader(body))
		r.Header.Set("Authorization", "Bearer "+key)
		w := httptest.NewRecorder()
		d.ServeHTTP(w, r)
		if w.Code != want {
			t.Fatalf("%s %s returned %d want %d", endpoint, q.Action, w.Code, want)
		}
		var s webSignal
		if w.Code == 200 {
			if err := json.Unmarshal(w.Body.Bytes(), &s); err != nil {
				t.Fatal(err)
			}
		}
		return s
	}
	offer := webSignal{"offer", id, desc}
	call("connect", room, offer, 404)
	call("host", owner, webSignal{Action: "poll"}, 200)
	call("connect", room, webSignal{"offer", id, webDescription{SDP: "media bytes"}}, 400)
	call("connect", room, offer, 200)
	call("connect", room, offer, 200)
	call("connect", room, webSignal{"offer", strings.Repeat("c", 64), desc}, 409)
	if got := call("host", owner, webSignal{Action: "poll"}, 200); got.Description != desc || got.ID != id {
		t.Fatal("lost offer")
	}
	call("host", room, webSignal{"answer", id, desc}, 404)
	call("host", owner, webSignal{"answer", id, desc}, 204)
	if got := call("connect", room, webSignal{Action: "poll", ID: id}, 200); got.Action != "answer" {
		t.Fatal("lost answer")
	}
	call("host", owner, webSignal{Action: "close"}, 204)
	call("connect", room, offer, 404)
	call("host", owner, webSignal{Action: "poll"}, 200)
	call("connect", room, offer, 200)
	call("host", owner, webSignal{Action: "reject", ID: id}, 204)
	if got := call("connect", room, webSignal{Action: "poll", ID: id}, 200); got.Action != "reject" {
		t.Fatal("missing rejection")
	}
	now = now.Add(100 * time.Second)
	call("connect", room, offer, 404)
}
