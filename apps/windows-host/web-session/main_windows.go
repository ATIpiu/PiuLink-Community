// The tray starts this process in the interactive Windows user's session. Closing
// its stdin ends capture, releases held input, and closes the peer connection.
package main

import (
	"bufio"
	"bytes"
	"context"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"sync/atomic"
	"time"

	"github.com/pion/webrtc/v4"
)

type description struct {
	SDP string `json:"sdp"`
	MAC string `json:"mac"`
}
type signal struct {
	Action      string      `json:"action"`
	ID          string      `json:"id,omitempty"`
	Description description `json:"description,omitempty"`
}
type decision struct {
	ID    string `json:"id"`
	Allow bool   `json:"allow"`
}
type directoryClient struct {
	base, owner string
	client      *http.Client
}

func (d directoryClient) post(ctx context.Context, q signal) (signal, error) {
	var result signal
	body, err := json.Marshal(q)
	if err != nil {
		return result, err
	}
	req, err := http.NewRequestWithContext(ctx, "POST", d.base+"web/v1/host", bytes.NewReader(body))
	if err != nil {
		return result, err
	}
	req.Header.Set("Authorization", "Bearer "+d.owner)
	req.Header.Set("Content-Type", "application/json")
	r, err := d.client.Do(req)
	if err != nil {
		return result, errors.New("无法连接打洞服务")
	}
	defer r.Body.Close()
	if r.StatusCode == 204 {
		return result, nil
	}
	if r.StatusCode != 200 {
		return result, fmt.Errorf("打洞服务 HTTP %d，请确认服务已支持网页访问", r.StatusCode)
	}
	err = json.NewDecoder(io.LimitReader(r.Body, 32768)).Decode(&result)
	return result, err
}
func proof(key []byte, role, id, sdp string) []byte {
	h := hmac.New(sha256.New, key)
	fmt.Fprintf(h, "piulink-web-1\n%s\n%s\n%s", role, id, sdp)
	return h.Sum(nil)
}
func notify(kind, value string) {
	p, _ := json.Marshal(map[string]string{"type": kind, "value": value})
	fmt.Println(string(p))
}
func run(ctx context.Context, base string, decisions <-chan decision) error {
	u, err := url.Parse(base)
	if err != nil || u.Scheme != "https" || u.Hostname() == "" || u.User != nil || u.Path != "/piulink/" || u.RawQuery != "" || u.Fragment != "" {
		return errors.New("打洞服务地址无效")
	}
	owner := make([]byte, 32)
	key := make([]byte, 32)
	rand.Read(owner)
	rand.Read(key)
	dir := directoryClient{base, hex.EncodeToString(owner), &http.Client{Timeout: 6 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}}
	room := sha256.Sum256([]byte("piulink-web-room\n" + dir.owner))
	until := time.Now().Add(10 * time.Minute)
	first := true
	rejected := false
	reject := func(id string) { _, err := dir.post(ctx, signal{Action: "reject", ID: id}); rejected = err == nil }
	defer func() {
		if rejected {
			return
		}
		t, c := context.WithTimeout(context.Background(), 2*time.Second)
		defer c()
		dir.post(t, signal{Action: "close"})
	}()
	for time.Now().Before(until) {
		reply, err := dir.post(ctx, signal{Action: "poll"})
		if err != nil {
			if first {
				return err
			}
		} else {
			if first {
				notify("ready", base+"web#"+hex.EncodeToString(room[:])+"."+hex.EncodeToString(key))
				first = false
			}
			if reply.Action == "offer" {
				mac, de := hex.DecodeString(reply.Description.MAC)
				if de != nil || len(reply.ID) != 64 || len(reply.Description.SDP) > 24000 || !hmac.Equal(mac, proof(key, "offer", reply.ID, reply.Description.SDP)) {
					reject(reply.ID)
					return errors.New("网页邀请校验失败，已关闭此次访问")
				}
				notify("approval", reply.ID)
				select {
				case <-ctx.Done():
					return nil
				case <-time.After(40 * time.Second):
					reject(reply.ID)
					return errors.New("等待本机确认超时")
				case v := <-decisions:
					if v.ID != reply.ID || !v.Allow {
						reject(reply.ID)
						return nil
					}
				}
				return servePeer(ctx, u, dir, key, reply)
			}
		}
		select {
		case <-ctx.Done():
			return nil
		case <-time.After(time.Second):
		}
	}
	return errors.New("网页邀请已过期")
}
func servePeer(ctx context.Context, u *url.URL, dir directoryClient, key []byte, offer signal) error {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	host := u.Hostname()
	if dir.base == "https://atipiu.org:8443/piulink/" {
		host = "172.245.182.117"
	}
	var settings webrtc.SettingEngine
	if err := settings.SetEphemeralUDPPortRange(52620, 52635); err != nil {
		return err
	}
	pc, err := webrtc.NewAPI(webrtc.WithSettingEngine(settings)).NewPeerConnection(webrtc.Configuration{ICEServers: []webrtc.ICEServer{{URLs: []string{"stun:" + net.JoinHostPort(host, "3478")}}}})
	if err != nil {
		return err
	}
	defer pc.Close()
	input := newDesktopInput()
	defer input.stop()
	opened := make(chan struct{}, 1)
	var claimed atomic.Bool
	pc.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
		switch s {
		case webrtc.PeerConnectionStateFailed, webrtc.PeerConnectionStateClosed:
			cancel()
		case webrtc.PeerConnectionStateDisconnected:
			input.release()
			time.AfterFunc(10*time.Second, func() {
				if pc.ConnectionState() == webrtc.PeerConnectionStateDisconnected {
					cancel()
				}
			})
		}
	})
	pc.OnDataChannel(func(dc *webrtc.DataChannel) {
		if dc.Label() != "desktop" || !dc.Ordered() || !claimed.CompareAndSwap(false, true) {
			dc.Close()
			return
		}
		dc.OnOpen(func() {
			select {
			case opened <- struct{}{}:
			default:
				dc.Close()
				return
			}
			notify("connected", "")
			go func() {
				if err := frames(ctx, dc); err != nil && ctx.Err() == nil {
					notify("error", "无法读取当前桌面")
					cancel()
				}
			}()
		})
		dc.OnClose(cancel)
		dc.OnMessage(func(m webrtc.DataChannelMessage) {
			if m.IsString && len(m.Data) <= 2048 && ctx.Err() == nil {
				input.handle(m.Data)
			}
		})
	})
	if err = pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: offer.Description.SDP}); err != nil {
		return errors.New("浏览器协商信息无效")
	}
	ans, err := pc.CreateAnswer(nil)
	if err != nil {
		return err
	}
	gather := webrtc.GatheringCompletePromise(pc)
	if err = pc.SetLocalDescription(ans); err != nil {
		return err
	}
	select {
	case <-ctx.Done():
		return nil
	case <-gather:
	case <-time.After(6 * time.Second):
	}
	sdp := pc.LocalDescription().SDP
	answer := signal{Action: "answer", ID: offer.ID, Description: description{sdp, hex.EncodeToString(proof(key, "answer", offer.ID, sdp))}}
	if _, err = dir.post(ctx, answer); err != nil {
		if _, err = dir.post(ctx, answer); err != nil {
			return err
		}
	}
	select {
	case <-ctx.Done():
		return nil
	case <-opened:
	case <-time.After(25 * time.Second):
		return errors.New("两端未建立可用的直连路径")
	}
	dir.post(ctx, signal{Action: "close"})
	<-ctx.Done()
	return nil
}
func main() {
	reader := bufio.NewScanner(os.Stdin)
	reader.Buffer(make([]byte, 2048), 4096)
	if !reader.Scan() {
		return
	}
	var cfg struct {
		Service string `json:"service"`
	}
	if json.Unmarshal(reader.Bytes(), &cfg) != nil || strings.TrimSpace(cfg.Service) == "" {
		notify("error", "打洞服务地址无效")
		return
	}
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	decisions := make(chan decision, 1)
	go func() {
		defer cancel()
		for reader.Scan() {
			var d decision
			if json.Unmarshal(reader.Bytes(), &d) == nil {
				select {
				case decisions <- d:
				case <-ctx.Done():
					return
				}
			}
		}
	}()
	if err := run(ctx, cfg.Service, decisions); err != nil && ctx.Err() == nil {
		notify("error", err.Error())
	}
}
