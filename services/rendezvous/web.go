package main

import (
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"time"
)

//go:embed web.html
var webPage []byte

//go:embed web.js
var webScript []byte

type webRoom struct {
	until, offerUntil time.Time
	id, state, peer   string
	offer, answer     webDescription
}
type webDescription struct {
	SDP string `json:"sdp"`
	MAC string `json:"mac"`
}

func (s webDescription) valid() bool {
	return len(s.SDP) <= 24000 && strings.HasPrefix(s.SDP, "v=0\r\n") &&
		strings.Contains(s.SDP, "\r\nm=application ") && strings.Contains(s.SDP, "a=fingerprint:sha-256 ") &&
		credential.MatchString(s.MAC)
}

type webSignal struct {
	Action      string         `json:"action"`
	ID          string         `json:"id,omitempty"`
	Description webDescription `json:"description,omitempty"`
}

func (d *directory) serveWeb(w http.ResponseWriter, r *http.Request) bool {
	host := r.URL.Path == prefix+"web/v1/host"
	connect := r.URL.Path == prefix+"web/v1/connect"
	if !host && !connect && r.URL.Path != prefix+"web" && r.URL.Path != prefix+"web.js" {
		return false
	}
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'")
	if r.URL.RawQuery != "" {
		http.NotFound(w, r)
		return true
	}
	if !host && !connect {
		if r.Method != "GET" && r.Method != "HEAD" {
			w.WriteHeader(405)
			return true
		}
		data, mime := webPage, "text/html; charset=utf-8"
		if strings.HasSuffix(r.URL.Path, ".js") {
			data, mime = webScript, "text/javascript; charset=utf-8"
		}
		w.Header().Set("Content-Type", mime)
		if r.Method == "GET" {
			w.Write(data)
		}
		return true
	}
	if r.Method != "POST" {
		w.WriteHeader(405)
		return true
	}
	key, ok := strings.CutPrefix(r.Header.Get("Authorization"), "Bearer ")
	if !ok || !credential.MatchString(key) {
		w.WriteHeader(401)
		return true
	}
	var q webSignal
	decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 32768))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&q) != nil || !errors.Is(decoder.Decode(new(any)), io.EOF) {
		w.WriteHeader(400)
		return true
	}
	if host {
		digest := sha256.Sum256([]byte("piulink-web-room\n" + key))
		key = hex.EncodeToString(digest[:])
	}
	var result *webSignal
	d.Lock()
	defer func() {
		d.Unlock()
		if result != nil {
			json.NewEncoder(w).Encode(result)
		}
	}()
	now := d.now()
	if d.webRooms == nil {
		d.webRooms = make(map[string]*webRoom)
	}
	for k, v := range d.webRooms {
		if !now.Before(v.until) {
			delete(d.webRooms, k)
		}
	}
	room := d.webRooms[key]
	if host && q.Action == "poll" {
		if room == nil {
			if len(d.webRooms) >= 256 {
				w.WriteHeader(503)
				return true
			}
			peer := r.Header.Get("X-PiuLink-Peer")
			count := 0
			for _, v := range d.webRooms {
				if v.peer == peer {
					count++
				}
			}
			if count >= 32 {
				w.WriteHeader(429)
				return true
			}
			room = &webRoom{peer: peer}
			d.webRooms[key] = room
		}
		room.until = now.Add(90 * time.Second)
	}
	if room == nil {
		w.WriteHeader(404)
		return true
	}
	if room.id != "" && !now.Before(room.offerUntil) {
		room.id = ""
		room.state = ""
		room.offer = webDescription{}
		room.answer = webDescription{}
	}
	w.Header().Set("Content-Type", "application/json")
	reply := func(state string, desc webDescription) {
		result = &webSignal{Action: state, ID: room.id, Description: desc}
	}
	switch {
	case host && q.Action == "close":
		delete(d.webRooms, key)
		w.WriteHeader(204)
	case host && q.Action == "poll":
		if room.state == "waiting" {
			reply("offer", room.offer)
		} else {
			reply("idle", webDescription{})
		}
	case host && (q.Action == "answer" || q.Action == "reject"):
		if room.id == "" || room.id != q.ID {
			w.WriteHeader(409)
			break
		}
		if q.Action == "answer" && !q.Description.valid() {
			w.WriteHeader(400)
			break
		}
		if room.state != "waiting" && (room.state != q.Action || room.answer != q.Description) {
			w.WriteHeader(409)
			break
		}
		room.state = q.Action
		room.answer = q.Description
		w.WriteHeader(204)
	case connect && (q.Action == "offer" || q.Action == "poll"):
		if !credential.MatchString(q.ID) {
			w.WriteHeader(400)
			break
		}
		if q.Action == "offer" {
			if !q.Description.valid() {
				w.WriteHeader(400)
				break
			}
			if room.id == "" {
				room.id = q.ID
				room.offer = q.Description
				room.state = "waiting"
				room.offerUntil = now.Add(75 * time.Second)
			}
			if room.offer != q.Description {
				w.WriteHeader(409)
				break
			}
		}
		if room.id != q.ID {
			w.WriteHeader(409)
			break
		}
		reply(room.state, room.answer)
	default:
		w.WriteHeader(400)
	}
	return true
}
