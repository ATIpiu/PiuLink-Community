// Produce a content-addressed block map from the exact file uploaded to a GitHub release.
package main

import (
	"compress/gzip"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/atipiu/piulink/packages/tunnel/updates"
)

func main() {
	if len(os.Args) != 5 {
		fmt.Fprintln(os.Stderr, "usage: piulink-update-map INSTALLER MAP_DIR VERSION RELEASE_TAG")
		os.Exit(2)
	}
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
func run() error {
	f, e := os.Open(os.Args[1])
	if e != nil {
		return e
	}
	defer f.Close()
	st, e := f.Stat()
	if e != nil {
		return e
	}
	h := sha256.New()
	blocks, e := updates.Map(io.TeeReader(f, h))
	if e != nil {
		return e
	}
	if e = os.MkdirAll(os.Args[2], 0755); e != nil {
		return e
	}
	tmp, e := os.CreateTemp(os.Args[2], ".map-")
	if e != nil {
		return e
	}
	defer func() { tmp.Close(); os.Remove(tmp.Name()) }()
	mh := sha256.New()
	gz := gzip.NewWriter(io.MultiWriter(tmp, mh))
	if e = json.NewEncoder(gz).Encode(blocks); e != nil {
		return e
	}
	if e = gz.Close(); e != nil {
		return e
	}
	if e = tmp.Close(); e != nil {
		return e
	}
	digest := fmt.Sprintf("%x", mh.Sum(nil))
	target := filepath.Join(os.Args[2], digest+".gz")
	if e = os.Rename(tmp.Name(), target); e != nil {
		return e
	}
	return json.NewEncoder(os.Stdout).Encode(updates.Release{Version: os.Args[3], Tag: os.Args[4], Name: st.Name(), Bytes: st.Size(), SHA256: fmt.Sprintf("%x", h.Sum(nil)), MapSHA256: digest})
}
