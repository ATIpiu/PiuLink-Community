package main

import (
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestHostInvite(t *testing.T) {
	now := time.Now()
	d := directory{rooms: map[string]entry{}, now: func() time.Time { return now }}
	owner := strings.Repeat("a", 64)
	id := "12345678-1234-1234-1234-123456789abc"
	room := roomKey(owner, id)
	if room != "459617d8c4ee175243e93401e6f519aac468220ae2e04d14fae2457320c2ff53" {
		t.Fatal("room derivation differs from client protocol")
	}
	call := func(path, key, body string, want int) string {
		t.Helper()
		req := httptest.NewRequest("POST", prefix+"v1/"+path, strings.NewReader(body))
		req.Header.Set("Authorization", "Bearer "+key)
		w := httptest.NewRecorder()
		d.ServeHTTP(w, req)
		if w.Code != want {
			t.Fatalf("%s: got %d, want %d", path, w.Code, want)
		}
		return w.Body.String()
	}
	doc := `{"hostId":"` + id + `","addresses":["192.168.1.2","2001:db8::2"]}`
	call("resolve", room, "", 404)
	call("publish", owner, doc, 204)
	call("publish", room, strings.Replace(doc, "192.168.1.2", "192.168.2.2", 1), 204)
	if got := call("resolve", room, "", 200); !strings.Contains(got, "192.168.1.2") {
		t.Fatal("reader overwrote host")
	}
	call("publish", owner, strings.Replace(doc, "192.168.1.2", "127.0.0.1", 1), 400)
	call("publish", owner, doc+doc, 400)
	call("publish", owner, strings.Repeat(" ", 2050)+doc, 400)
	call("resolve", owner, "", 404)
	call("relay", room, "", 404)
	now = now.Add(lease)
	call("resolve", room, "", 404)
	call("publish", owner, doc, 204)
	call("resolve", room, "", 200)
}
