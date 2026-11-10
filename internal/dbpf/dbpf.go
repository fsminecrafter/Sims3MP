// Package dbpf writes (and reads back) the two things a Sims 3 script mod
// package contains: the assembly as an S3SA resource and its tuning XML.
//
// The layout follows the publicly documented DBPF 2.0 container and the
// S3SA script resource as understood by community tools. It has not been
// checked against a package produced by S3PE; see docs/BUILDING.md.
package dbpf

import (
	"bytes"
	"encoding/binary"
	"errors"
	"strings"
)

const (
	TypeS3SA = 0x073FAA07 // script assembly
	TypeXML  = 0x0333406C // tuning XML
)

type Resource struct {
	Type, Group uint32
	Instance    uint64
	Data        []byte
}

// FNV64 is the game's name hash: 64-bit FNV-1 over the lower-cased name.
func FNV64(name string) uint64 {
	h := uint64(0xCBF29CE484222325)
	for _, c := range []byte(strings.ToLower(name)) {
		h *= 0x100000001B3
		h ^= uint64(c)
	}
	return h
}

// ScriptResource wraps an assembly in an S3SA resource. The format allows
// the payload to be scrambled with a key table; an all-zero table means the
// 512-byte blocks are stored as they are.
func ScriptResource(assembly []byte) []byte {
	blocks := (len(assembly) + 511) / 512
	var b bytes.Buffer
	b.WriteByte(1)                                            // version
	binary.Write(&b, binary.LittleEndian, uint32(0x2BC4F79F)) // constant
	b.Write(make([]byte, 64))                                 // checksum (unused by the game)
	binary.Write(&b, binary.LittleEndian, uint16(blocks))
	b.Write(make([]byte, blocks*8)) // key table: zeros = not scrambled
	b.Write(assembly)
	b.Write(make([]byte, blocks*512-len(assembly)))
	return b.Bytes()
}

// Write builds an uncompressed DBPF 2.0 package.
func Write(res []Resource) []byte {
	const header = 96
	var data, index bytes.Buffer
	le := binary.LittleEndian
	binary.Write(&index, le, uint32(0)) // index type: every field stored per entry
	for _, r := range res {
		for _, v := range []uint32{r.Type, r.Group, uint32(r.Instance >> 32), uint32(r.Instance),
			uint32(header + data.Len()), uint32(len(r.Data)) | 0x80000000, uint32(len(r.Data))} {
			binary.Write(&index, le, v)
		}
		binary.Write(&index, le, uint16(0)) // not compressed
		binary.Write(&index, le, uint16(1))
		data.Write(r.Data)
	}
	h := make([]byte, header)
	copy(h, "DBPF")
	le.PutUint32(h[4:], 2)                          // major version
	le.PutUint32(h[36:], uint32(len(res)))          // index entry count
	le.PutUint32(h[44:], uint32(index.Len()))       // index size
	le.PutUint32(h[60:], 3)                         // index version
	le.PutUint32(h[64:], uint32(header+data.Len())) // index position
	return append(append(h, data.Bytes()...), index.Bytes()...)
}

// Read parses a package written by Write (used to self-check the output).
func Read(p []byte) ([]Resource, error) {
	le := binary.LittleEndian
	if len(p) < 96 || string(p[:4]) != "DBPF" || le.Uint32(p[4:]) != 2 {
		return nil, errors.New("dbpf: not a DBPF 2.0 package")
	}
	n, size, pos := int(le.Uint32(p[36:])), int(le.Uint32(p[44:])), int(le.Uint32(p[64:]))
	if pos < 96 || pos+size > len(p) || size != 4+32*n || le.Uint32(p[pos:]) != 0 {
		return nil, errors.New("dbpf: unsupported index")
	}
	var out []Resource
	for i := 0; i < n; i++ {
		e := p[pos+4+32*i:]
		off, sz := int(le.Uint32(e[16:])), int(le.Uint32(e[20:])&0x7FFFFFFF)
		if off < 96 || off+sz > pos || le.Uint16(e[28:]) != 0 {
			return nil, errors.New("dbpf: bad entry")
		}
		out = append(out, Resource{Type: le.Uint32(e[0:]), Group: le.Uint32(e[4:]),
			Instance: uint64(le.Uint32(e[8:]))<<32 | uint64(le.Uint32(e[12:])), Data: p[off : off+sz]})
	}
	return out, nil
}
