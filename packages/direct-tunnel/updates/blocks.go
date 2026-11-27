// Package updates reconstructs the official release asset by reusing verified chunks of a local installer.
package updates

import (
	"bufio"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"io"
)

const maxBlock = 256 << 10

type Block struct {
	Size int    `json:"n"`
	Hash string `json:"h"`
}

// Fixed gear table and boundaries are part of the v1 on-disk format.
var gear = func() (table [256]uint64) {
	x := uint64(0x6a09e667f3bcc909)
	for i := range table {
		x ^= x << 13
		x ^= x >> 7
		x ^= x << 17
		table[i] = x
	}
	return
}()

func scan(r io.Reader, visit func(int64, Block) error) error {
	input := bufio.NewReaderSize(r, 128<<10)
	buf := make([]byte, 0, maxBlock)
	var offset int64
	var roll uint64
	for {
		b, err := input.ReadByte()
		if err != nil && !errors.Is(err, io.EOF) {
			return err
		}
		if err == nil {
			buf = append(buf, b)
			roll = (roll << 1) + gear[b]
			if len(buf) < maxBlock && (len(buf) < 16<<10 || roll&0xffff != 0) {
				continue
			}
		}
		if len(buf) > 0 {
			sum := sha256.Sum256(buf)
			if e := visit(offset, Block{len(buf), hex.EncodeToString(sum[:])}); e != nil {
				return e
			}
			offset += int64(len(buf))
			buf, roll = buf[:0], 0
		}
		if err != nil {
			return nil
		}
	}
}

func Map(r io.Reader) ([]Block, error) {
	blocks := make([]Block, 0)
	err := scan(r, func(_ int64, b Block) error { blocks = append(blocks, b); return nil })
	return blocks, err
}
