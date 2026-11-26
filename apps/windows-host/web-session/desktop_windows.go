package main

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"image"
	"image/jpeg"
	"math"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode/utf16"
	"unsafe"

	"github.com/kbinani/screenshot"
	"github.com/pion/webrtc/v4"
	"golang.org/x/sys/windows"
)

var user32 = windows.NewLazySystemDLL("user32.dll")
var sendInput = user32.NewProc("SendInput")
var getMetric = user32.NewProc("GetSystemMetrics")
var openDesktop = user32.NewProc("OpenInputDesktop")
var desktopInfo = user32.NewProc("GetUserObjectInformationW")
var closeDesktop = user32.NewProc("CloseDesktop")

// Stop on session lock or a secure desktop switch. Input belongs only to the
// ordinary signed-in desktop that the Host user explicitly shared.
func interactiveDesktop() bool {
	handle, _, _ := openDesktop.Call(0, 0, 1)
	if handle == 0 {
		return false
	}
	defer closeDesktop.Call(handle)
	var name [64]uint16
	var size uint32
	ok, _, _ := desktopInfo.Call(handle, 2, uintptr(unsafe.Pointer(&name[0])), unsafe.Sizeof(name), uintptr(unsafe.Pointer(&size)))
	return ok != 0 && strings.EqualFold(windows.UTF16ToString(name[:]), "Default")
}

func frames(ctx context.Context, dc *webrtc.DataChannel) error {
	user32.NewProc("SetProcessDPIAware").Call()
	tick := time.NewTicker(180 * time.Millisecond)
	defer tick.Stop()
	var previous [32]byte
	for {
		select {
		case <-ctx.Done():
			return nil
		case <-tick.C:
		}
		if !interactiveDesktop() {
			return errors.New("desktop locked or switched")
		}
		if dc.BufferedAmount() > 65536 {
			continue
		}
		w, _, _ := getMetric.Call(0)
		h, _, _ := getMetric.Call(1)
		if w == 0 || h == 0 || w*h > 64000000 {
			return errors.New("invalid display")
		}
		full, err := screenshot.CaptureRect(image.Rect(0, 0, int(w), int(h)))
		if err != nil {
			return err
		}
		scale := math.Min(1, math.Min(1280/float64(w), 800/float64(h)))
		width, height := max(1, int(float64(w)*scale)), max(1, int(float64(h)*scale))
		frame := full
		if width != int(w) || height != int(h) {
			frame = image.NewRGBA(image.Rect(0, 0, width, height))
			for y := 0; y < height; y++ {
				src := y * int(h) / height * full.Stride
				dst := y * frame.Stride
				for x := 0; x < width; x++ {
					i := src + (x*int(w)/width)*4
					copy(frame.Pix[dst+x*4:dst+x*4+4], full.Pix[i:i+4])
				}
			}
		}
		sum := sha256.Sum256(frame.Pix)
		if sum == previous {
			continue
		}
		var data bytes.Buffer
		if err = jpeg.Encode(&data, frame, &jpeg.Options{Quality: 65}); err != nil {
			return err
		}
		if data.Len() > 524288 {
			data.Reset()
			if err = jpeg.Encode(&data, frame, &jpeg.Options{Quality: 38}); err != nil {
				return err
			}
		}
		if data.Len() > 524288 {
			continue
		}
		if err = dc.SendText(fmt.Sprintf(`{"size":%d}`, data.Len())); err != nil {
			return err
		}
		for data.Len() > 0 {
			if err = dc.Send(data.Next(16384)); err != nil {
				return err
			}
		}
		previous = sum
	}
}

// INPUT is 40 bytes on Windows x64. The union starts at an eight-byte boundary.
type winInput struct {
	kind    uint32
	payload [4]uintptr
}
type mouseData struct {
	x, y               int32
	data, flags, stamp uint32
	extra              uintptr
}
type keyData struct {
	vk, scan     uint16
	flags, stamp uint32
	extra        uintptr
}

func mouse(flags uint32, x, y int32, data uint32) {
	var in winInput
	*(*mouseData)(unsafe.Pointer(&in.payload[0])) = mouseData{x: x, y: y, data: data, flags: flags}
	sendInput.Call(1, uintptr(unsafe.Pointer(&in)), unsafe.Sizeof(in))
}
func key(vk, scan uint16, flags uint32) {
	in := winInput{kind: 1}
	*(*keyData)(unsafe.Pointer(&in.payload[0])) = keyData{vk: vk, scan: scan, flags: flags}
	sendInput.Call(1, uintptr(unsafe.Pointer(&in)), unsafe.Sizeof(in))
}

var keys = map[string]uint16{"Enter": 13, "Escape": 27, "Backspace": 8, "Tab": 9, "Space": 32, "Delete": 46, "Insert": 45,
	"ArrowLeft": 37, "ArrowUp": 38, "ArrowRight": 39, "ArrowDown": 40, "Home": 36, "End": 35, "PageUp": 33, "PageDown": 34,
	"ShiftLeft": 160, "ShiftRight": 161, "ControlLeft": 162, "ControlRight": 163, "AltLeft": 164, "AltRight": 165,
	"Minus": 189, "Equal": 187, "BracketLeft": 219, "BracketRight": 221, "Backslash": 220, "Semicolon": 186,
	"Quote": 222, "Backquote": 192, "Comma": 188, "Period": 190, "Slash": 191}

func virtualKey(code string) uint16 {
	if len(code) == 4 && strings.HasPrefix(code, "Key") && code[3] >= 'A' && code[3] <= 'Z' {
		return uint16(code[3])
	}
	if len(code) == 6 && strings.HasPrefix(code, "Digit") && code[5] >= '0' && code[5] <= '9' {
		return uint16(code[5])
	}
	if strings.HasPrefix(code, "F") {
		if n, err := strconv.Atoi(code[1:]); err == nil && n >= 1 && n <= 12 {
			return uint16(111 + n)
		}
	}
	return keys[code]
}
func physicalKey(vk uint16, up bool) {
	var flags uint32
	if up {
		flags = 2
	}
	if (vk >= 33 && vk <= 40) || vk == 45 || vk == 46 || vk == 163 || vk == 165 {
		flags |= 1
	}
	key(vk, 0, flags)
}

var buttons = [3][2]uint32{{2, 4}, {32, 64}, {8, 16}}

type desktopInput struct {
	mu      sync.Mutex
	held    map[uint16]bool
	buttons [3]bool
	stopped bool
}

func newDesktopInput() *desktopInput { return &desktopInput{held: map[uint16]bool{}} }
func (d *desktopInput) releaseLocked() {
	for vk := range d.held {
		physicalKey(vk, true)
		delete(d.held, vk)
	}
	for i, v := range d.buttons {
		if v {
			mouse(buttons[i][1], 0, 0, 0)
			d.buttons[i] = false
		}
	}
}
func (d *desktopInput) release() { d.mu.Lock(); defer d.mu.Unlock(); d.releaseLocked() }
func (d *desktopInput) stop()    { d.mu.Lock(); defer d.mu.Unlock(); d.stopped = true; d.releaseLocked() }
func (d *desktopInput) handle(data []byte) {
	var v struct {
		Type, Code, Text string
		X, Y             float64
		Button, Delta    int
	}
	if json.Unmarshal(data, &v) != nil {
		return
	}
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.stopped || !interactiveDesktop() {
		return
	}
	switch v.Type {
	case "release":
		d.releaseLocked()
	case "move", "down", "up":
		if v.X < 0 || v.X > 1 || v.Y < 0 || v.Y > 1 {
			return
		}
		mouse(0x8001, int32(math.Round(v.X*65535)), int32(math.Round(v.Y*65535)), 0)
		if v.Type == "move" || v.Button < 0 || v.Button >= len(buttons) {
			return
		}
		if v.Type == "down" && !d.buttons[v.Button] {
			d.buttons[v.Button] = true
			mouse(buttons[v.Button][0], 0, 0, 0)
		}
		if v.Type == "up" && d.buttons[v.Button] {
			d.buttons[v.Button] = false
			mouse(buttons[v.Button][1], 0, 0, 0)
		}
	case "wheel":
		if v.Delta == 1 || v.Delta == -1 {
			mouse(0x0800, 0, 0, uint32(int32(v.Delta*120)))
		}
	case "keydown", "keyup":
		vk := virtualKey(v.Code)
		if vk == 0 {
			return
		}
		if v.Type == "keydown" {
			physicalKey(vk, false)
			d.held[vk] = true
		} else if d.held[vk] {
			physicalKey(vk, true)
			delete(d.held, vk)
		}
	case "text":
		units := utf16.Encode([]rune(v.Text))
		if len(units) > 256 {
			return
		}
		for _, u := range units {
			if u >= 32 {
				key(0, u, 4)
				key(0, u, 6)
			}
		}
	}
}
