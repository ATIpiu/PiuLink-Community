package main

import (
	"crypto/rand"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"regexp"
	"sync"
	"time"
)

type offer struct {
	ID        string `json:"id"`
	Time      int64  `json:"time"`
	Cert      string `json:"cert"`
	Wrapped   string `json:"wrapped"`
	Signature string `json:"signature"`
}
type iceInfo struct {
	User        string   `json:"user"`
	Password    string   `json:"password"`
	Candidates  []string `json:"candidates"`
	Fingerprint string   `json:"fingerprint"`
	Proof       string   `json:"proof"`
}
type call struct {
	room        string
	offer       offer
	expires     time.Time
	tickets     [2]string
	ice         [2]*iceInfo
	lastOffered time.Time
}
type punchState struct {
	sync.Mutex
	calls map[string]*call
	hosts map[string]hostPoll
}
type hostPoll struct {
	cert  string
	until time.Time
}

var punches = punchState{calls: map[string]*call{}, hosts: map[string]hostPoll{}}
var sidPattern = regexp.MustCompile("^[a-f0-9]{32}$")
var candidatePattern = regexp.MustCompile(`^[[:alnum:]+/]{1,32} 1 (?i:udp) [0-9]+ [0-9a-fA-F:.]+ [0-9]+ typ (host|srflx)( raddr [0-9a-fA-F:.]+ rport [0-9]+)?( ufrag [[:alnum:]+/_-]{1,128})?( generation [0-9]+)?( network-id [0-9]+)?( network-cost [0-9]+)?$`)

func newTicket() string { var b [32]byte; rand.Read(b[:]); return hex.EncodeToString(b[:]) }
func decodeSmall(w http.ResponseWriter, r *http.Request, v any) bool {
	d := json.NewDecoder(http.MaxBytesReader(w, r.Body, 8192))
	d.DisallowUnknownFields()
	if d.Decode(v) != nil || d.Decode(new(any)) != io.EOF {
		w.WriteHeader(400)
		return false
	}
	return true
}
func validCert(s string) bool {
	b, err := base64.StdEncoding.DecodeString(s)
	if err != nil || len(b) > 3072 {
		return false
	}
	_, err = x509.ParseCertificate(b)
	return err == nil
}
func sendJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	json.NewEncoder(w).Encode(v)
}
func (d *directory) online(room string) bool {
	d.Lock()
	defer d.Unlock()
	e, ok := d.rooms[room]
	return ok && d.now().Before(e.until)
}
func (s *punchState) prune(now time.Time) {
	for id, c := range s.calls {
		if !now.Before(c.expires) {
			delete(s.calls, id)
		}
	}
	for id, h := range s.hosts {
		if !now.Before(h.until) {
			delete(s.hosts, id)
		}
	}
}
func (s *punchState) serve(w http.ResponseWriter, r *http.Request, key string, d *directory) {
	now := d.now()
	switch r.URL.Path {
	case prefix + "v2/host":
		var req struct {
			HostID string `json:"hostId"`
			Cert   string `json:"cert"`
		}
		if !decodeSmall(w, r, &req) {
			return
		}
		room := roomKey(key, req.HostID)
		if !d.online(room) {
			w.WriteHeader(404)
			return
		}
		if !validCert(req.Cert) {
			w.WriteHeader(400)
			return
		}
		s.Lock()
		defer s.Unlock()
		s.prune(now)
		s.hosts[room] = hostPoll{req.Cert, now.Add(15 * time.Second)}
		// Receiving the Host's authenticated ICE description acknowledges the offer.
		// A lost poll reply must not consume it. New offers take precedence over retries.
		var next *call
		for _, c := range s.calls {
			if c.room != room || c.ice[1] != nil || now.Sub(c.lastOffered) < 4*time.Second {
				continue
			}
			if next == nil || c.lastOffered.Before(next.lastOffered) {
				next = c
			}
		}
		if next != nil {
			next.lastOffered = now
			sendJSON(w, map[string]any{"offer": next.offer, "ticket": next.tickets[1]})
			return
		}
		w.WriteHeader(204)
	case prefix + "v2/identity":
		s.Lock()
		h := s.hosts[key]
		s.Unlock()
		if !now.Before(h.until) || !d.online(key) {
			w.WriteHeader(404)
			return
		}
		sendJSON(w, map[string]string{"cert": h.cert})
	case prefix + "v2/open":
		if !d.online(key) {
			w.WriteHeader(404)
			return
		}
		var o offer
		if !decodeSmall(w, r, &o) {
			return
		}
		if !sidPattern.MatchString(o.ID) || o.Time < now.Unix()-45 || o.Time > now.Unix()+45 || len(o.Wrapped) > 768 || len(o.Signature) > 768 || !validCert(o.Cert) {
			w.WriteHeader(400)
			return
		}
		s.Lock()
		defer s.Unlock()
		s.prune(now)
		if _, ok := s.hosts[key]; !ok {
			w.WriteHeader(404)
			return
		}
		if existing := s.calls[o.ID]; existing != nil {
			// The opening reply can be lost after allocation. Only an identical
			// signed request in the same room may recover its source ticket.
			if existing.room == key && existing.offer == o {
				sendJSON(w, map[string]string{"ticket": existing.tickets[0]})
				return
			}
			w.WriteHeader(409)
			return
		}
		n := 0
		for _, c := range s.calls {
			if c.room == key {
				n++
			}
		}
		if n >= 8 || len(s.calls) >= 512 {
			w.WriteHeader(429)
			return
		}
		c := &call{room: key, offer: o, expires: now.Add(65 * time.Second), tickets: [2]string{newTicket(), newTicket()}}
		s.calls[o.ID] = c
		sendJSON(w, map[string]string{"ticket": c.tickets[0]})
	case prefix + "v2/exchange":
		var req struct {
			ID   string  `json:"id"`
			Info iceInfo `json:"info"`
		}
		if !decodeSmall(w, r, &req) {
			return
		}
		s.Lock()
		defer s.Unlock()
		c := s.calls[req.ID]
		if c == nil || !now.Before(c.expires) {
			w.WriteHeader(404)
			return
		}
		role := 0
		if key == c.tickets[1] {
			role = 1
		} else if key != c.tickets[0] {
			w.WriteHeader(403)
			return
		}
		info := req.Info
		if len(info.Candidates) < 1 || len(info.Candidates) > 24 || len(info.User) > 128 || len(info.Password) > 128 || len(info.Proof) != 43 || (role == 1 && !credential.MatchString(info.Fingerprint)) || (role == 0 && info.Fingerprint != "") {
			w.WriteHeader(400)
			return
		}
		for _, cand := range info.Candidates {
			if len(cand) > 240 || !candidatePattern.MatchString(cand) {
				w.WriteHeader(400)
				return
			}
		}
		if c.ice[role] == nil {
			c.ice[role] = &info
		}
		if peer := c.ice[1-role]; peer != nil {
			sendJSON(w, peer)
		} else {
			w.WriteHeader(204)
		}
	default:
		http.NotFound(w, r)
	}
}
