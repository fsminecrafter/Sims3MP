//go:build windows

package voice

import (
	"errors"
	"fmt"
	"syscall"
	"time"
	"unsafe"
)

// Audio I/O through winmm (waveIn/waveOut). It is available on every Windows
// version the game supports and needs no cgo or extra DLLs.

var (
	winmm                 = syscall.NewLazyDLL("winmm.dll")
	kernel32              = syscall.NewLazyDLL("kernel32.dll")
	pVirtualAlloc         = kernel32.NewProc("VirtualAlloc")
	pWaveInOpen           = winmm.NewProc("waveInOpen")
	pWaveInPrepareHeader  = winmm.NewProc("waveInPrepareHeader")
	pWaveInAddBuffer      = winmm.NewProc("waveInAddBuffer")
	pWaveInStart          = winmm.NewProc("waveInStart")
	pWaveInReset          = winmm.NewProc("waveInReset")
	pWaveInClose          = winmm.NewProc("waveInClose")
	pWaveOutOpen          = winmm.NewProc("waveOutOpen")
	pWaveOutPrepareHeader = winmm.NewProc("waveOutPrepareHeader")
	pWaveOutWrite         = winmm.NewProc("waveOutWrite")
	pWaveOutReset         = winmm.NewProc("waveOutReset")
	pWaveOutClose         = winmm.NewProc("waveOutClose")
)

type waveFormat struct {
	FormatTag      uint16
	Channels       uint16
	SamplesPerSec  uint32
	AvgBytesPerSec uint32
	BlockAlign     uint16
	BitsPerSample  uint16
	Size           uint16
}

type waveHdr struct {
	Data          uintptr
	BufferLength  uint32
	BytesRecorded uint32
	User          uintptr
	Flags         uint32
	Loops         uint32
	Next          uintptr
	Reserved      uintptr
}

const (
	whdrDone   = 1
	nBuffers   = 8
	waveMapper = ^uintptr(0)
)

// ring is a set of wave headers and sample buffers in memory that the Go
// runtime does not manage, because the driver keeps pointers to it.
type ring struct {
	hdr  [nBuffers]*waveHdr
	data [nBuffers][]int16
	used [nBuffers]bool
	next int
}

func newRing(samples int) (*ring, error) {
	hs := unsafe.Sizeof(waveHdr{})
	per := hs + uintptr(samples*2)
	base, _, err := pVirtualAlloc.Call(0, per*nBuffers, 0x3000, 4)
	if base == 0 {
		return nil, err
	}
	r := &ring{}
	for i := 0; i < nBuffers; i++ {
		p := unsafe.Add(unsafe.Pointer(base), uintptr(i)*per) //nolint:govet // OS memory, never moved
		r.hdr[i] = (*waveHdr)(p)
		d := unsafe.Add(p, hs)
		r.hdr[i].Data = uintptr(d)
		r.hdr[i].BufferLength = uint32(samples * 2)
		r.data[i] = unsafe.Slice((*int16)(d), samples)
	}
	return r, nil
}

type winDevice struct {
	in, out   uintptr
	inR, outR *ring
	closed    bool
}

func format(ch uint16) waveFormat {
	return waveFormat{FormatTag: 1, Channels: ch, SamplesPerSec: SampleRate,
		AvgBytesPerSec: SampleRate * 2 * uint32(ch), BlockAlign: 2 * ch, BitsPerSample: 16}
}

// OpenDevice opens the default microphone and speakers. A missing microphone
// is not fatal: the device is then listen-only.
func OpenDevice() (Device, error) {
	d := &winDevice{}
	hs := unsafe.Sizeof(waveHdr{})
	of := format(2)
	if rc, _, _ := pWaveOutOpen.Call(uintptr(unsafe.Pointer(&d.out)), waveMapper, uintptr(unsafe.Pointer(&of)), 0, 0, 0); rc != 0 {
		return nil, fmt.Errorf("waveOutOpen failed (%d)", rc)
	}
	var err error
	if d.outR, err = newRing(FrameSamples * 2); err != nil {
		return nil, err
	}
	for _, h := range d.outR.hdr {
		pWaveOutPrepareHeader.Call(d.out, uintptr(unsafe.Pointer(h)), hs)
	}
	inf := format(1)
	if rc, _, _ := pWaveInOpen.Call(uintptr(unsafe.Pointer(&d.in)), waveMapper, uintptr(unsafe.Pointer(&inf)), 0, 0, 0); rc == 0 {
		if d.inR, err = newRing(FrameSamples); err != nil {
			return nil, err
		}
		for _, h := range d.inR.hdr {
			pWaveInPrepareHeader.Call(d.in, uintptr(unsafe.Pointer(h)), hs)
			pWaveInAddBuffer.Call(d.in, uintptr(unsafe.Pointer(h)), hs)
		}
		pWaveInStart.Call(d.in)
	} else {
		d.in = 0
	}
	return d, nil
}

var errClosed = errors.New("voice: device closed")

func (d *winDevice) Read(frame []int16) error {
	if d.in == 0 {
		time.Sleep(time.Second)
		return errors.New("voice: no microphone")
	}
	r := d.inR
	h := r.hdr[r.next]
	for h.Flags&whdrDone == 0 {
		if d.closed {
			return errClosed
		}
		time.Sleep(2 * time.Millisecond)
	}
	n := copy(frame, r.data[r.next][:h.BytesRecorded/2])
	for i := n; i < len(frame); i++ {
		frame[i] = 0
	}
	pWaveInAddBuffer.Call(d.in, uintptr(unsafe.Pointer(h)), unsafe.Sizeof(waveHdr{}))
	r.next = (r.next + 1) % nBuffers
	return nil
}

func (d *winDevice) Write(stereo []int16) error {
	r := d.outR
	h := r.hdr[r.next]
	for r.used[r.next] && h.Flags&whdrDone == 0 {
		if d.closed {
			return errClosed
		}
		time.Sleep(2 * time.Millisecond)
	}
	copy(r.data[r.next], stereo)
	r.used[r.next] = true
	pWaveOutWrite.Call(d.out, uintptr(unsafe.Pointer(h)), unsafe.Sizeof(waveHdr{}))
	r.next = (r.next + 1) % nBuffers
	return nil
}

func (d *winDevice) Close() {
	d.closed = true
	if d.in != 0 {
		pWaveInReset.Call(d.in)
		pWaveInClose.Call(d.in)
	}
	pWaveOutReset.Call(d.out)
	pWaveOutClose.Call(d.out)
}
