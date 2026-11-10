// Package voice implements proximity voice chat for the client bridge:
// a tiny codec (16 kHz mono G.711 mu-law, 20 ms frames), a voice-activity
// gate, and a positional mixer that attenuates and pans each speaker by
// where their sim is relative to the listener.
package voice

import (
	"math"
	"sync"
)

const (
	SampleRate   = 16000
	FrameSamples = 320 // 20 ms
)

// --- codec ---------------------------------------------------------------

func encodeSample(s int16) byte {
	const bias, clip = 0x84, 32635
	v := int(s)
	sign := 0
	if v < 0 {
		v, sign = -v, 0x80
	}
	if v > clip {
		v = clip
	}
	v += bias
	exp := 7
	for mask := 0x4000; exp > 0 && v&mask == 0; mask >>= 1 {
		exp--
	}
	return ^byte(sign | exp<<4 | (v>>(exp+3))&0x0F)
}

var decodeTable = func() (t [256]int16) {
	for i := range t {
		u := ^byte(i)
		v := (int(u&0x0F)<<3 + 0x84) << (u >> 4 & 7)
		v -= 0x84
		if u&0x80 != 0 {
			v = -v
		}
		t[i] = int16(v)
	}
	return
}()

func Encode(dst []byte, pcm []int16) []byte {
	for _, s := range pcm {
		dst = append(dst, encodeSample(s))
	}
	return dst
}

func Decode(dst []int16, data []byte) []int16 {
	for _, b := range data {
		dst = append(dst, decodeTable[b])
	}
	return dst
}

// --- voice activity gate -------------------------------------------------

// Gate opens when a frame is loud enough and stays open for a short hangover
// so word endings are not clipped.
type Gate struct {
	Threshold float64 // RMS, 0..32767
	Hangover  int     // frames
	left      int
}

func (g *Gate) Open(pcm []int16) bool {
	var sum float64
	for _, s := range pcm {
		sum += float64(s) * float64(s)
	}
	if math.Sqrt(sum/float64(len(pcm)+1)) >= g.Threshold {
		g.left = g.Hangover
		return true
	}
	if g.left > 0 {
		g.left--
		return true
	}
	return false
}

// --- positional mixer ----------------------------------------------------

type Vec3 struct{ X, Y, Z float32 }

// Gains returns left/right gain for a speaker at src heard from listener
// looking along (fwdX, fwdZ) on the ground plane. Full volume inside ref,
// silent at max, with a smooth inverse-distance roll-off between.
func Gains(listener, src Vec3, fwdX, fwdZ float32, ref, max float64) (l, r float64) {
	dx, dy, dz := float64(src.X-listener.X), float64(src.Y-listener.Y), float64(src.Z-listener.Z)
	d := math.Sqrt(dx*dx + dy*dy + dz*dz)
	if d >= max {
		return 0, 0
	}
	g := 1.0
	if d > ref {
		g = ref / d                  // inverse distance
		g *= (max - d) / (max - ref) // fade so it reaches exactly 0 at max
	}
	pan := 0.0
	if h := math.Hypot(dx, dz); h > 0.5 {
		fl := math.Hypot(float64(fwdX), float64(fwdZ))
		if fl > 1e-3 {
			// right-hand vector of the listener on the ground plane
			rx, rz := -float64(fwdZ)/fl, float64(fwdX)/fl
			pan = (dx*rx + dz*rz) / h
		}
	}
	// constant-power pan, limited so a voice never vanishes from one ear
	a := (pan*0.8 + 1) * math.Pi / 4
	return g * math.Cos(a) * math.Sqrt2, g * math.Sin(a) * math.Sqrt2
}

type speaker struct {
	frames  [][]int16
	pos     Vec3
	playing bool
	lastSeq uint16
	idle    int
}

// Mixer holds a small jitter buffer per remote player.
type Mixer struct {
	mu         sync.Mutex
	speakers   map[uint32]*speaker
	listener   Vec3
	fwdX, fwdZ float32
	Ref, Max   float64
	SwapLR     bool
}

func NewMixer(ref, max float64) *Mixer {
	return &Mixer{speakers: map[uint32]*speaker{}, Ref: ref, Max: max, fwdZ: 1}
}

func (m *Mixer) SetListener(p Vec3, fwdX, fwdZ float32) {
	m.mu.Lock()
	m.listener, m.fwdX, m.fwdZ = p, fwdX, fwdZ
	m.mu.Unlock()
}

// Push queues one received voice packet.
func (m *Mixer) Push(player uint32, seq uint16, pos Vec3, audio []byte) {
	if len(audio) != FrameSamples {
		return
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	sp := m.speakers[player]
	if sp == nil {
		sp = &speaker{}
		m.speakers[player] = sp
	} else if d := int16(seq - sp.lastSeq); d <= 0 && d > -50 {
		return // duplicate or reordered packet
	}
	sp.lastSeq, sp.pos, sp.idle = seq, pos, 0
	if len(sp.frames) >= 8 { // too far behind real time: skip ahead
		sp.frames = sp.frames[4:]
	}
	sp.frames = append(sp.frames, Decode(make([]int16, 0, FrameSamples), audio))
}

// Mix fills out (interleaved stereo, 2*FrameSamples) with the next 20 ms.
func (m *Mixer) Mix(out []int16) {
	var acc [2 * FrameSamples]float64
	m.mu.Lock()
	for id, sp := range m.speakers {
		if !sp.playing {
			if len(sp.frames) < 2 { // 40 ms of jitter protection
				if sp.idle++; sp.idle > 500 {
					delete(m.speakers, id)
				}
				continue
			}
			sp.playing = true
		}
		if len(sp.frames) == 0 {
			sp.playing = false
			continue
		}
		f := sp.frames[0]
		sp.frames = sp.frames[1:]
		l, r := Gains(m.listener, sp.pos, m.fwdX, m.fwdZ, m.Ref, m.Max)
		if m.SwapLR {
			l, r = r, l
		}
		for i, s := range f {
			acc[2*i] += float64(s) * l
			acc[2*i+1] += float64(s) * r
		}
	}
	m.mu.Unlock()
	for i := range out {
		v := acc[i]
		if v > 32767 {
			v = 32767
		} else if v < -32768 {
			v = -32768
		}
		out[i] = int16(v)
	}
}

// Device is a full-duplex audio endpoint: 16 kHz mono in, 16 kHz stereo out.
type Device interface {
	Read(frame []int16) error   // blocks for one FrameSamples frame
	Write(stereo []int16) error // blocks while the output queue is full
	Close()
}
