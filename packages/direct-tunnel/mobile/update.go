package mobile

import (
	"encoding/json"
	"github.com/atipiu/piulink/packages/tunnel/updates"
)

// CheckUpdate returns JSON null when the installed channel is current.
func CheckUpdate(channel, installed string) (string, error) {
	r, e := updates.Check(channel, installed)
	if e != nil {
		return "", e
	}
	b, e := json.Marshal(r)
	return string(b), e
}

// PrepareUpdate returns the path and byte counts of a verified, complete installer.
func PrepareUpdate(channel, installed, cache, seed string) (string, error) {
	r, e := updates.Prepare(channel, installed, cache, seed)
	if e != nil {
		return "", e
	}
	b, e := json.Marshal(r)
	return string(b), e
}
