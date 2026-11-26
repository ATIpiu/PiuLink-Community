package mobile

import (
	"errors"
	"net"
	"testing"
	"time"
)

type bindFunc func(int) error

func (f bindFunc) Bind(fd int) error { return f(fd) }

func TestBoundUDPSocket(t *testing.T) {
	loopback := &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)}
	denied := errors.New("network denied")
	calls := 0
	network := &boundUDPNetwork{binder: bindFunc(func(fd int) error {
		calls++
		if fd < 0 {
			t.Fatal("missing socket")
		}
		return denied
	})}
	if conn, err := network.ListenUDP("udp4", loopback); !errors.Is(err, denied) || conn != nil {
		t.Fatalf("denied socket returned %v, %v", conn, err)
	}
	network.binder = bindFunc(func(int) error { calls++; return nil })
	conn, err := network.ListenUDP("udp4", loopback)
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	if calls != 2 {
		t.Fatalf("binder called %d times", calls)
	}
	peer, err := net.DialUDP("udp4", nil, conn.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatal(err)
	}
	defer peer.Close()
	if _, err := peer.Write([]byte{42}); err != nil {
		t.Fatal(err)
	}
	conn.SetReadDeadline(time.Now().Add(time.Second))
	var b [1]byte
	if n, _, err := conn.ReadFromUDP(b[:]); err != nil || n != 1 || b[0] != 42 {
		t.Fatalf("UDP read returned %d, %v, byte %d", n, err, b[0])
	}
}
