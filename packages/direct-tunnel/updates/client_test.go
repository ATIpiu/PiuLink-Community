package updates

import (
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"io"
	"math/rand"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

type roundTrip func(*http.Request) (*http.Response, error)

func (f roundTrip) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func hash(data []byte) string                                         { return fmt.Sprintf("%x", sha256.Sum256(data)) }

func TestVerifiedRangeUpdate(t *testing.T) {
	old := make([]byte, 4<<20)
	rand.New(rand.NewSource(19)).Read(old)
	target := append([]byte(nil), old...)
	copy(target[1<<20:], bytes.Repeat([]byte("new release"), 12000))
	blocks, e := Map(bytes.NewReader(target))
	if e != nil {
		t.Fatal(e)
	}
	var packed bytes.Buffer
	z := gzip.NewWriter(&packed)
	if e = json.NewEncoder(z).Encode(blocks); e != nil {
		t.Fatal(e)
	}
	if e = z.Close(); e != nil {
		t.Fatal(e)
	}
	rel := Release{"0.2.10", "release-10", "installer.zip", int64(len(target)), hash(target), hash(packed.Bytes())}
	cat, _ := json.Marshal(map[string]Release{"client": rel})
	saved := httpClient
	t.Cleanup(func() { httpClient = saved })
	for _, mode := range []string{"range", "no-seed", "identical", "full-response", "bad-map", "bad-range", "truncated", "corrupt"} {
		t.Run(mode, func(t *testing.T) {
			cache := t.TempDir()
			seed := ""
			if mode != "no-seed" {
				seed = filepath.Join(cache, "old.zip")
				data := old
				if mode == "identical" {
					data = target
				}
				if err := os.WriteFile(seed, data, 0600); err != nil {
					t.Fatal(err)
				}
			}
			var received int64
			httpClient = &http.Client{Transport: roundTrip(func(r *http.Request) (*http.Response, error) {
				var body []byte
				status := 200
				h := make(http.Header)
				switch r.URL.String() {
				case raw + "channels.json":
					body = cat
				case raw + "maps/" + rel.MapSHA256 + ".gz":
					body = packed.Bytes()
					if mode == "bad-map" {
						body = []byte("bad")
					}
				case release + rel.Tag + "/" + rel.Name:
					var first, last int
					if n, _ := fmt.Sscanf(r.Header.Get("Range"), "bytes=%d-%d", &first, &last); n != 2 || first < 0 || last < first || last >= len(target) {
						t.Fatalf("bad request range")
					}
					status = 206
					body = target[first : last+1]
					h.Set("Content-Range", fmt.Sprintf("bytes %d-%d/%d", first, last, len(target)))
					switch mode {
					case "full-response":
						status = 200
						body = target
					case "bad-range":
						h.Set("Content-Range", "invalid")
					case "truncated":
						body = body[:len(body)-1]
					case "corrupt":
						body = append([]byte(nil), body...)
						body[0] ^= 0xff
					}
					received += int64(len(body))
				default:
					t.Fatalf("unexpected URL %v", r.URL)
				}
				return &http.Response{StatusCode: status, Header: h, Body: io.NopCloser(bytes.NewReader(body)), Request: r}, nil
			})}
			out, err := Prepare("client", "0.2.9", cache, seed)
			if strings.HasPrefix(mode, "bad-") || mode == "truncated" || mode == "corrupt" {
				if err == nil || out != nil {
					t.Fatal("accepted corrupt update")
				}
				entries, _ := os.ReadDir(filepath.Join(cache, "client"))
				if len(entries) != 0 {
					t.Fatal("left unverified output")
				}
				return
			}
			if err != nil || digestFile(out.Path) != rel.SHA256 {
				t.Fatalf("update failed: %v", err)
			}
			if received != out.Downloaded || out.Downloaded+out.Reused != int64(len(target)) {
				t.Fatal("incorrect byte accounting")
			}
			if mode == "range" && (received == 0 || received > int64(len(target)/2)) {
				t.Fatalf("unexpected transfer: %d", received)
			}
			already, e := Prepare("client", "0.2.9", cache, "")
			if e != nil || already.Downloaded != 0 || already.Path != out.Path {
				t.Fatal("cache reuse failed")
			}
			for _, v := range []string{"0.2.10", "0.3.0", "1.0.0"} {
				offered, e := Check("client", v)
				if e != nil || offered != nil {
					t.Fatalf("offered downgrade to %s", v)
				}
			}
		})
	}
}
