package tunnel

import "testing"

func TestReplayWindowWithReorderedVideo(t *testing.T) {
	var window replayWindow
	for _, sequence := range []uint64{1, 3, 2, 4097, 4096, 8192, 4098} {
		if !window.accept(sequence) {
			t.Fatalf("rejected new packet %d", sequence)
		}
		if window.accept(sequence) {
			t.Fatalf("accepted duplicate %d", sequence)
		}
	}
	if window.accept(0) || window.accept(4096) || window.accept(3) {
		t.Fatal("accepted expired sequence")
	}
}
