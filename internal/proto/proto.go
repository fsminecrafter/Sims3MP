// Package proto defines the Sims3MP wire protocol shared by the dedicated
// server, the client bridge and (re-implemented in C#) the script mod.
//
// TCP: frames of [u32 length][u8 type][payload], little endian, where length
// counts the type byte plus the payload.
// UDP: datagrams of [u8 type][u64 session key][payload].
package proto

import (
	"encoding/binary"
	"errors"
	"io"
	"math"
	"path"
	"strings"
)

const (
	Version   = 1
	MaxFrame  = 1 << 20
	ChunkSize = 64 << 10
)

// Client -> server (TCP).
const (
	CHello       = 1  // u16 version, str name, str identitySecret, str password
	CUploadBegin = 3  // u8 kind (admin)
	CFileBegin   = 4  // u8 kind, str path, u64 size, [32]sha256 (also server -> client)
	CFileChunk   = 5  // raw bytes (also server -> client)
	CFileEnd     = 6  // (also server -> client)
	CUploadEnd   = 7  // u8 kind (admin)
	CRequestFile = 9  // u8 kind, str path
	CReady       = 10 // [u64 lastSeq]: world is loaded; send the snapshot and every logged op after lastSeq
	COp          = 11 // u8 category, u64 lot, u64 entity, bytes payload
	CSetSleeping = 12 // u8 allAsleep
	CSetSpeed    = 13 // u8 mode (admin)
	CClaimLot    = 14 // u64 lot
	CReleaseLot  = 15 // u64 lot
	CSetBuilder  = 16 // u64 lot, str playerName, u8 add
	CClaimEntity = 17 // u64 entity (household or sim)
	CSetShared   = 18 // str key, bytes value
	CChat        = 19 // str text
	CPing        = 20 // u64 clientTime
	CKick        = 21 // str playerName (admin)
	CSetLotOwner = 22 // u64 lot, str playerName ("" clears) (admin)
	CReleaseEnt  = 23 // u64 entity
	CEntityData  = 24 // u64 entity, u8 slot, bytes: latest-wins state of an entity the sender owns
)

// Server -> client (TCP).
const (
	SWelcome     = 64 // u32 playerID, u8 admin, u64 udpKey, u8 state, u32 epoch
	SReject      = 65 // str reason
	SPlayerJoin  = 66 // u32 id, str name, u8 admin
	SPlayerLeave = 67 // u32 id
	SFileList    = 68 // u8 kind, u32 epoch, u32 n, n*(str path, u64 size, [32]sha)
	SState       = 69 // u8 state, u32 epoch
	SClock       = 70 // u64 simMillis, u8 speed, u8 mode
	SOp          = 71 // u64 seq, u32 fromPlayer, u8 category, u64 lot, u64 entity, bytes payload
	SOpRejected  = 72 // u8 category, u64 lot, u64 entity, str reason
	SLot         = 73 // u64 lot, str owner, u16 n, n*str builder
	SEntity      = 74 // u64 entity, str owner
	SShared      = 75 // str key, bytes value
	SChat        = 76 // u32 from, str text
	SPong        = 77 // u64 clientTime
	SNotice      = 78 // str text
	SSnapshotEnd = 79 // sent after the snapshot that follows CReady
	SEntityData  = 80 // u64 entity, u8 slot, bytes
)

// UDP datagram types.
const (
	UState      = 1 // c->s: u64 entity, payload
	UStateRelay = 2 // s->c: u32 player, u64 entity, payload
	UVoice      = 3 // c->s: u16 seq, f32 x,y,z, audio
	UVoiceRelay = 4 // s->c: u32 player, u16 seq, f32 x,y,z, audio
	UPosition   = 5 // c->s: f32 x,y,z (listener position; also keeps NAT mapping alive)
	UAck        = 7 // s->c: empty, confirms the UDP path
)

// Messages between the script mod and the local bridge (never sent to a server).
const (
	LConnect     = 128 // str host:port, str name, str password
	LDisconnect  = 129
	LHostWorld   = 130 // str saveName: upload mods + save, then server starts running
	LState       = 131 // u64 entity, payload (sent via UDP)
	LListener    = 132 // f32 x,y,z, f32 fwdX, fwdZ
	LVoice       = 133 // u8 micEnabled, u8 speakerEnabled
	LStartServer = 134 // u16 port: spawn sims3mp-server next to the bridge and connect to it
	LQuery       = 135 // ask for a fresh LStatus

	LStatus     = 160 // u8 phase, str text, u32 playerID, u8 admin, str saveName, str playerName
	LStateRelay = 161 // u32 player, u64 entity, payload
)

// Bridge phases reported in LStatus.
const (
	PhaseIdle = iota
	PhaseConnecting
	PhaseWaitingForWorld
	PhaseSyncing
	PhaseRestartRequired
	PhaseWorldReady
	PhaseError
)

// Server states.
const (
	StateWaitingForWorld = 0
	StateInitializing    = 1
	StateRunning         = 2
)

// File kinds.
const (
	KindWorld = 0
	KindMods  = 1
)

// Op categories. The server relays the payload untouched but enforces these.
const (
	OpGeneral = 0 // entity (if non-zero) must be owned by the sender
	OpBuild   = 1 // sender must own the lot or be one of its builders
	OpAdmin   = 2 // admin only (e.g. NRaas master-controller actions)
	OpWorld   = 3 // anyone

	// OpTransient can be OR-ed into a category: the op is relayed in order
	// but not recorded, so it is never replayed to players who join later
	// (e.g. "this sim started that interaction").
	OpTransient = 0x80
)

// Speed modes for CSetSpeed / SClock.
const (
	SpeedPause = 0
	SpeedAuto  = 255 // 1x, or 3x while every player is asleep
)

type W struct{ B []byte }

func (w *W) U8(v uint8) *W     { w.B = append(w.B, v); return w }
func (w *W) U16(v uint16) *W   { w.B = binary.LittleEndian.AppendUint16(w.B, v); return w }
func (w *W) U32(v uint32) *W   { w.B = binary.LittleEndian.AppendUint32(w.B, v); return w }
func (w *W) U64(v uint64) *W   { w.B = binary.LittleEndian.AppendUint64(w.B, v); return w }
func (w *W) F32(v float32) *W  { return w.U32(math.Float32bits(v)) }
func (w *W) Raw(b []byte) *W   { w.B = append(w.B, b...); return w }
func (w *W) Bytes(b []byte) *W { return w.U32(uint32(len(b))).Raw(b) }
func (w *W) Str(s string) *W {
	if len(s) > 0xFFFF {
		s = s[:0xFFFF]
	}
	w.U16(uint16(len(s)))
	w.B = append(w.B, s...)
	return w
}
func (w *W) Bool(v bool) *W {
	if v {
		return w.U8(1)
	}
	return w.U8(0)
}

// R is a bounds-checked reader; after any short read Err is set and all
// further reads return zero values.
type R struct {
	B   []byte
	Err bool
}

func (r *R) take(n int) []byte {
	if r.Err || n < 0 || len(r.B) < n {
		r.Err = true
		return nil
	}
	b := r.B[:n]
	r.B = r.B[n:]
	return b
}
func (r *R) U8() uint8 {
	if b := r.take(1); b != nil {
		return b[0]
	}
	return 0
}
func (r *R) U16() uint16 {
	if b := r.take(2); b != nil {
		return binary.LittleEndian.Uint16(b)
	}
	return 0
}
func (r *R) U32() uint32 {
	if b := r.take(4); b != nil {
		return binary.LittleEndian.Uint32(b)
	}
	return 0
}
func (r *R) U64() uint64 {
	if b := r.take(8); b != nil {
		return binary.LittleEndian.Uint64(b)
	}
	return 0
}
func (r *R) F32() float32     { return math.Float32frombits(r.U32()) }
func (r *R) Bool() bool       { return r.U8() != 0 }
func (r *R) Str() string      { return string(r.take(int(r.U16()))) }
func (r *R) Bytes() []byte    { return r.take(int(r.U32())) }
func (r *R) Raw(n int) []byte { return r.take(n) }
func (r *R) Rest() []byte {
	b := r.B
	r.B = nil
	return b
}

// Frame builds a complete TCP frame.
func Frame(typ uint8, payload []byte) []byte {
	b := make([]byte, 5+len(payload))
	binary.LittleEndian.PutUint32(b, uint32(1+len(payload)))
	b[4] = typ
	copy(b[5:], payload)
	return b
}

var ErrFrame = errors.New("proto: bad frame length")

// ReadFrame reads one frame. The returned payload is freshly allocated.
func ReadFrame(r io.Reader) (uint8, []byte, error) {
	var hdr [5]byte
	if _, err := io.ReadFull(r, hdr[:]); err != nil {
		return 0, nil, err
	}
	n := binary.LittleEndian.Uint32(hdr[:4])
	if n < 1 || n > MaxFrame {
		return 0, nil, ErrFrame
	}
	p := make([]byte, n-1)
	if _, err := io.ReadFull(r, p); err != nil {
		return 0, nil, err
	}
	return hdr[4], p, nil
}

// Datagram builds a client->server UDP datagram.
func Datagram(typ uint8, key uint64, payload []byte) []byte {
	b := make([]byte, 9, 9+len(payload))
	b[0] = typ
	binary.LittleEndian.PutUint64(b[1:], key)
	return append(b, payload...)
}

// CleanRel validates a relative file path received from the network and
// returns it in slash form. It rejects anything that could escape the
// directory it is joined onto.
func CleanRel(p string) (string, bool) {
	p = strings.ReplaceAll(p, "\\", "/")
	if p == "" || len(p) > 400 || strings.ContainsAny(p, ":\x00") || strings.HasPrefix(p, "/") {
		return "", false
	}
	c := path.Clean(p)
	if c == "." || c == ".." || strings.HasPrefix(c, "../") {
		return "", false
	}
	return c, true
}

type FileEntry struct {
	Path string
	Size uint64
	Sum  [32]byte
}
