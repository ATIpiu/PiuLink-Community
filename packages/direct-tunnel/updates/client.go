package updates

import (
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

const raw = "https://raw.githubusercontent.com/ATIpiu/PiuLink-Community/main/updates/"
const release = "https://github.com/ATIpiu/PiuLink-Community/releases/download/"
const maxSize = 1 << 30

var safeName = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9_.-]{0,150}$`)
var hashRE = regexp.MustCompile(`^[a-f0-9]{64}$`)
var httpClient = &http.Client{Timeout: 10 * time.Minute, CheckRedirect: func(r *http.Request, via []*http.Request) error {
	h := r.URL.Hostname()
	if len(via) > 6 || r.URL.Scheme != "https" || (h != "github.com" && !strings.HasSuffix(h, ".githubusercontent.com")) {
		return errors.New("unexpected release redirect")
	}
	return nil
}}

type Release struct {
	Version   string `json:"version"`
	Tag       string `json:"tag"`
	Name      string `json:"name"`
	Bytes     int64  `json:"bytes"`
	SHA256    string `json:"sha256"`
	MapSHA256 string `json:"mapSha256"`
}
type Result struct {
	Path       string `json:"path"`
	Downloaded int64  `json:"downloaded"`
	Reused     int64  `json:"reused"`
}

func readURL(address string, limit int64) ([]byte, error) {
	resp, err := httpClient.Get(address)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("GitHub HTTP %d", resp.StatusCode)
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, limit+1))
	if int64(len(data)) > limit {
		return nil, errors.New("release metadata too large")
	}
	return data, err
}

// Public PiuLink versions use three numeric components, compared numerically.
func version(s string) ([3]int, bool) {
	var v [3]int
	parts := strings.Split(s, ".")
	if len(parts) != len(v) {
		return v, false
	}
	for i, p := range parts {
		n, e := strconv.Atoi(p)
		if e != nil || n < 0 || strconv.Itoa(n) != p {
			return v, false
		}
		v[i] = n
	}
	return v, true
}
func Check(channel, installed string) (*Release, error) {
	data, err := readURL(raw+"channels.json", 256<<10)
	if err != nil {
		return nil, err
	}
	var catalog map[string]Release
	if err = json.Unmarshal(data, &catalog); err != nil {
		return nil, err
	}
	r, ok := catalog[channel]
	a, valid := version(r.Version)
	b, current := version(installed)
	if !ok || !valid || !current || !safeName.MatchString(r.Tag) || !safeName.MatchString(r.Name) ||
		!hashRE.MatchString(r.SHA256) || !hashRE.MatchString(r.MapSHA256) || r.Bytes < 1 || r.Bytes > maxSize {
		return nil, errors.New("invalid release or unsupported update channel")
	}
	for i := range a {
		if a[i] > b[i] {
			return &r, nil
		}
		if a[i] < b[i] {
			break
		}
	}
	return nil, nil
}

type section struct{ start, end int64 }

func missing(blocks []Block, local map[Block]int64) []section {
	var spans []section
	var pos int64
	for _, b := range blocks {
		end := pos + int64(b.Size)
		if _, ok := local[b]; !ok {
			if len(spans) > 0 && pos-spans[len(spans)-1].end <= 64<<10 {
				spans[len(spans)-1].end = end
			} else {
				spans = append(spans, section{pos, end})
			}
		}
		pos = end
	}
	// Limit HTTP round trips by downloading the smallest intervening local gaps.
	for len(spans) > 32 {
		best := 0
		for i := 1; i < len(spans)-1; i++ {
			if spans[i+1].start-spans[i].end < spans[best+1].start-spans[best].end {
				best = i
			}
		}
		spans[best].end = spans[best+1].end
		spans = append(spans[:best+1], spans[best+2:]...)
	}
	return spans
}

func digestFile(path string) string {
	f, e := os.Open(path)
	if e != nil {
		return ""
	}
	defer f.Close()
	h := sha256.New()
	if _, e = io.Copy(h, f); e != nil {
		return ""
	}
	return hex.EncodeToString(h.Sum(nil))
}

// Prepare never installs or executes the result. A failed attempt only discards its own temporary file.
func Prepare(channel, installed, cache, seed string) (*Result, error) {
	r, err := Check(channel, installed)
	if err != nil {
		return nil, err
	}
	if r == nil {
		return nil, errors.New("already up to date")
	}
	return prepare(*r, channel, cache, seed)
}
func prepare(r Release, channel, cache, seed string) (*Result, error) {
	dir := filepath.Join(cache, channel)
	if !safeName.MatchString(channel) || !filepath.IsAbs(cache) {
		return nil, errors.New("invalid update directory")
	}
	if e := os.MkdirAll(dir, 0700); e != nil {
		return nil, e
	}
	path := filepath.Join(dir, r.Name)
	if digestFile(path) == r.SHA256 {
		return &Result{path, 0, r.Bytes}, nil
	}
	rawMap, e := readURL(raw+"maps/"+r.MapSHA256+".gz", 2<<20)
	if e != nil {
		return nil, e
	}
	if fmt.Sprintf("%x", sha256.Sum256(rawMap)) != r.MapSHA256 {
		return nil, errors.New("block map digest mismatch")
	}
	gz, e := gzip.NewReader(bytes.NewReader(rawMap))
	if e != nil {
		return nil, e
	}
	var blocks []Block
	e = json.NewDecoder(io.LimitReader(gz, 4<<20)).Decode(&blocks)
	gz.Close()
	if e != nil {
		return nil, e
	}
	var total int64
	for _, b := range blocks {
		if b.Size < 1 || b.Size > maxBlock || !hashRE.MatchString(b.Hash) {
			return nil, errors.New("invalid block")
		}
		total += int64(b.Size)
	}
	if total != r.Bytes {
		return nil, errors.New("invalid block map size")
	}
	if seed == "" {
		// Only consider verified release outputs in this channel, never temporary downloads.
		list, _ := os.ReadDir(dir)
		sort.Slice(list, func(i, j int) bool {
			a, ea := list[i].Info()
			b, eb := list[j].Info()
			return ea == nil && eb == nil && a.ModTime().After(b.ModTime())
		})
		for _, de := range list {
			if de.Type().IsRegular() && safeName.MatchString(de.Name()) && de.Name() != r.Name {
				seed = filepath.Join(dir, de.Name())
				break
			}
		}
	}
	local := map[Block]int64{}
	var base *os.File
	if seed != "" {
		base, e = os.Open(seed)
		if e != nil {
			return nil, e
		}
		defer base.Close()
		st, e := base.Stat()
		if e != nil || !st.Mode().IsRegular() || st.Size() > maxSize {
			return nil, errors.New("invalid base installer")
		}
		if e = scan(base, func(pos int64, b Block) error { local[b] = pos; return nil }); e != nil {
			return nil, e
		}
	}
	tmp, e := os.CreateTemp(dir, ".part-")
	if e != nil {
		return nil, e
	}
	defer func() { tmp.Close(); os.Remove(tmp.Name()) }()
	spans := missing(blocks, local)
	var network int64
	reused := r.Bytes
	var pos int64
	for _, b := range blocks {
		if off, ok := local[b]; ok {
			if _, e = tmp.Seek(pos, io.SeekStart); e != nil {
				return nil, e
			}
			if _, e = io.CopyN(tmp, io.NewSectionReader(base, off, int64(b.Size)), int64(b.Size)); e != nil {
				return nil, e
			}
		}
		pos += int64(b.Size)
	}
	address := release + url.PathEscape(r.Tag) + "/" + url.PathEscape(r.Name)
	for _, s := range spans {
		full, err := fetchRange(address, tmp, s, r.Bytes)
		if err != nil {
			return nil, err
		}
		if full {
			network += r.Bytes
			reused = 0
			break
		}
		network += s.end - s.start
		reused -= s.end - s.start
	}
	if e = tmp.Close(); e != nil {
		return nil, e
	}
	if digestFile(tmp.Name()) != r.SHA256 {
		return nil, errors.New("installer SHA-256 mismatch")
	}
	if e = os.Rename(tmp.Name(), path); e != nil && digestFile(path) != r.SHA256 {
		return nil, e
	}
	return &Result{path, network, reused}, nil
}
func fetchRange(address string, f *os.File, s section, total int64) (bool, error) {
	req, e := http.NewRequest(http.MethodGet, address, nil)
	if e != nil {
		return false, e
	}
	req.Header.Set("Range", fmt.Sprintf("bytes=%d-%d", s.start, s.end-1))
	req.Header.Set("Accept-Encoding", "identity")
	resp, e := httpClient.Do(req)
	if e != nil {
		return false, e
	}
	defer resp.Body.Close()
	full := resp.StatusCode == http.StatusOK
	if full {
		s = section{0, total}
	} else if resp.StatusCode != http.StatusPartialContent || resp.Header.Get("Content-Range") != fmt.Sprintf("bytes %d-%d/%d", s.start, s.end-1, total) {
		return false, fmt.Errorf("invalid GitHub range response (%d)", resp.StatusCode)
	}
	if _, e = f.Seek(s.start, io.SeekStart); e != nil {
		return false, e
	}
	n, e := io.Copy(f, io.LimitReader(resp.Body, s.end-s.start+1))
	if e == nil && n != s.end-s.start {
		e = errors.New("truncated or oversized release response")
	}
	return full, e
}
