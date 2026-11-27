// Invitation directory, bounded ICE signaling and a STUN Binding responder.
package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net"
	"net/http"
	"net/netip"
	"regexp"
	"strings"
	"sync"
	"time"
)

const prefix = "/piulink/"
const lease = 90 * time.Second
const maxRooms = 4096

var credential = regexp.MustCompile("^[0-9a-f]{64}$")
var uuid = regexp.MustCompile("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")

type presence struct {
	HostID    string   `json:"hostId"`
	Addresses []string `json:"addresses"`
}
type entry struct {
	presence
	until time.Time
	peer  string
}
type directory struct {
	sync.Mutex
	rooms    map[string]entry
	now      func() time.Time
	webRooms map[string]*webRoom
}

func roomKey(owner, hostID string) string {
	sum := sha256.Sum256([]byte(owner + "\n" + hostID))
	return hex.EncodeToString(sum[:])
}

func candidate(value string) (string, bool) {
	ip, err := netip.ParseAddr(value)
	if err != nil || ip.Zone() != "" {
		return "", false
	}
	ip = ip.Unmap()
	return ip.String(), ip.IsGlobalUnicast() && !(ip.Is4() && ip.As4()[0] == 0)
}

func (d *directory) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Referrer-Policy", "no-referrer")
	if d.serveWeb(w, r) {
		return
	}
	if r.Method == "GET" && r.URL.Path == prefix+"join" {
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		io.WriteString(w, "在 PiuLink 客户端选择「添加电脑」，粘贴完整邀请链接。首次连接请在 Host 确认配对码。")
		return
	}
	if r.Method != "POST" || r.URL.RawQuery != "" {
		http.NotFound(w, r)
		return
	}
	bearer, ok := strings.CutPrefix(r.Header.Get("Authorization"), "Bearer ")
	if !ok || !credential.MatchString(bearer) {
		w.WriteHeader(401)
		return
	}
	var p presence
	if strings.HasPrefix(r.URL.Path, prefix+"v2/") {
		punches.serve(w, r, bearer, d)
		return
	}
	switch r.URL.Path {
	case prefix + "v1/publish":
		decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 2048))
		decoder.DisallowUnknownFields()
		if decoder.Decode(&p) != nil || !errors.Is(decoder.Decode(new(any)), io.EOF) ||
			!uuid.MatchString(p.HostID) || len(p.Addresses) > 12 {
			w.WriteHeader(400)
			return
		}
		seen := map[string]bool{}
		addresses := []string{}
		for _, addr := range p.Addresses {
			ip, valid := candidate(addr)
			if !valid {
				w.WriteHeader(400)
				return
			}
			if !seen[ip] {
				seen[ip] = true
				addresses = append(addresses, ip)
			}
		}
		// The deployment proxy overwrites this header with its actual peer IP.
		if ip, valid := candidate(r.Header.Get("X-PiuLink-Peer")); valid && !seen[ip] {
			addresses = append(addresses, ip)
		}
		if len(addresses) == 0 {
			w.WriteHeader(400)
			return
		}
		p.Addresses = addresses
		key := roomKey(bearer, p.HostID)
		peer := r.Header.Get("X-PiuLink-Peer")
		d.Lock()
		defer d.Unlock()
		now := d.now()
		if previous, exists := d.rooms[key]; !exists || previous.peer != peer {
			count := 0
			for k, v := range d.rooms {
				if !now.Before(v.until) {
					delete(d.rooms, k)
				} else if v.peer == peer {
					count++
				}
			}
			for k, v := range d.webRooms {
				if !now.Before(v.until) {
					delete(d.webRooms, k)
				}
			}
			if count >= 32 {
				w.WriteHeader(429)
				return
			}
			if len(d.rooms) >= maxRooms {
				w.WriteHeader(503)
				return
			}
		}
		d.rooms[key] = entry{p, now.Add(lease), peer}
		w.WriteHeader(204)
	case prefix + "v1/resolve":
		if r.ContentLength != 0 {
			w.WriteHeader(400)
			return
		}
		d.Lock()
		e, exists := d.rooms[bearer]
		if exists && !d.now().Before(e.until) {
			delete(d.rooms, bearer)
			exists = false
		}
		d.Unlock()
		if !exists {
			w.WriteHeader(404)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(e.presence)
	default:
		http.NotFound(w, r)
	}
}

func main() {
	stun, err := net.ListenUDP("udp4", &net.UDPAddr{Port: 3478})
	if err != nil {
		log.Fatal(err)
	}
	defer stun.Close()
	go serveSTUN(stun)
	d := &directory{rooms: make(map[string]entry), now: time.Now}
	go func() {
		for now := range time.Tick(lease) {
			d.Lock()
			for k, v := range d.rooms {
				if !now.Before(v.until) {
					delete(d.rooms, k)
				}
			}
			d.Unlock()
		}
	}()
	server := &http.Server{Addr: "127.0.0.1:5086", Handler: d, ReadTimeout: 4 * time.Second,
		ReadHeaderTimeout: 3 * time.Second, WriteTimeout: 5 * time.Second, IdleTimeout: 10 * time.Second, MaxHeaderBytes: 4096}
	log.Fatal(server.ListenAndServe())
}
