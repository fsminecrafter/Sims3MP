// Package xfer implements directory scanning and chunked, hash-verified file
// transfer used for world saves and mod packages.
package xfer

import (
	"crypto/sha256"
	"errors"
	"hash"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"sort"

	"github.com/fsminecrafter/sims3mp/internal/proto"
)

// Scan lists and hashes every regular file below dir. A missing dir is empty.
func Scan(dir string) ([]proto.FileEntry, error) {
	var out []proto.FileEntry
	err := filepath.WalkDir(dir, func(p string, d fs.DirEntry, err error) error {
		if err != nil {
			if errors.Is(err, fs.ErrNotExist) && p == dir {
				return filepath.SkipAll
			}
			return err
		}
		if !d.Type().IsRegular() {
			return nil
		}
		rel, err := filepath.Rel(dir, p)
		if err != nil {
			return err
		}
		f, err := os.Open(p)
		if err != nil {
			return err
		}
		defer f.Close()
		h := sha256.New()
		n, err := io.Copy(h, f)
		if err != nil {
			return err
		}
		e := proto.FileEntry{Path: filepath.ToSlash(rel), Size: uint64(n)}
		h.Sum(e.Sum[:0])
		out = append(out, e)
		return nil
	})
	sort.Slice(out, func(i, j int) bool { return out[i].Path < out[j].Path })
	return out, err
}

// Send streams one file as FileBegin/FileChunk/FileEnd frames through emit.
func Send(dir string, kind uint8, e proto.FileEntry, emit func(frame []byte) error) error {
	f, err := os.Open(filepath.Join(dir, filepath.FromSlash(e.Path)))
	if err != nil {
		return err
	}
	defer f.Close()
	w := (&proto.W{}).U8(kind).Str(e.Path).U64(e.Size).Raw(e.Sum[:])
	if err := emit(proto.Frame(proto.CFileBegin, w.B)); err != nil {
		return err
	}
	buf := make([]byte, proto.ChunkSize)
	for {
		n, err := f.Read(buf)
		if n > 0 {
			if err := emit(proto.Frame(proto.CFileChunk, buf[:n])); err != nil {
				return err
			}
		}
		if err == io.EOF {
			break
		}
		if err != nil {
			return err
		}
	}
	return emit(proto.Frame(proto.CFileEnd, nil))
}

// Receiver writes one incoming file at a time below a base directory.
type Receiver struct {
	f     *os.File
	h     hash.Hash
	path  string
	final string
	left  uint64
	sum   [32]byte
	Kind  uint8
}

var ErrTransfer = errors.New("xfer: protocol violation")

// Active reports whether a file is currently being received.
func (r *Receiver) Active() bool { return r.f != nil }

// Begin parses a FileBegin payload; dirFor maps the kind to a base directory.
func (r *Receiver) Begin(payload []byte, dirFor func(kind uint8) (string, bool)) (string, error) {
	if r.f != nil {
		return "", ErrTransfer
	}
	rd := proto.R{B: payload}
	kind, p, size := rd.U8(), rd.Str(), rd.U64()
	sum := rd.Raw(32)
	rel, ok := proto.CleanRel(p)
	if rd.Err || !ok {
		return "", ErrTransfer
	}
	dir, ok := dirFor(kind)
	if !ok {
		return "", ErrTransfer
	}
	final := filepath.Join(dir, filepath.FromSlash(rel))
	if err := os.MkdirAll(filepath.Dir(final), 0o755); err != nil {
		return "", err
	}
	f, err := os.Create(final + ".part")
	if err != nil {
		return "", err
	}
	r.f, r.h, r.path, r.final, r.left, r.Kind = f, sha256.New(), rel, final, size, kind
	copy(r.sum[:], sum)
	return rel, nil
}

func (r *Receiver) Chunk(b []byte) error {
	if r.f == nil || uint64(len(b)) > r.left {
		r.Abort()
		return ErrTransfer
	}
	r.left -= uint64(len(b))
	r.h.Write(b)
	_, err := r.f.Write(b)
	return err
}

// End verifies size and hash and moves the file into place.
func (r *Receiver) End() (string, error) {
	if r.f == nil {
		return "", ErrTransfer
	}
	var got [32]byte
	r.h.Sum(got[:0])
	if r.left != 0 || got != r.sum {
		r.Abort()
		return "", errors.New("xfer: size or checksum mismatch for " + r.path)
	}
	err := r.f.Close()
	r.f = nil
	if err != nil {
		return "", err
	}
	return r.path, os.Rename(r.final+".part", r.final)
}

func (r *Receiver) Abort() {
	if r.f != nil {
		r.f.Close()
		os.Remove(r.final + ".part")
		r.f = nil
	}
}
