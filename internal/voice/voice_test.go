package voice

import (
	"math"
	"testing"
)

func TestCodecRoundTrip(t *testing.T) {
	for _, s := range []int16{0, 1, -1, 100, -100, 1000, -1000, 12345, -12345, 32767, -32768} {
		got := Decode(nil, Encode(nil, []int16{s}))[0]
		if err := math.Abs(float64(got) - float64(s)); err > math.Max(16, math.Abs(float64(s))*0.07) {
			t.Errorf("%d decoded as %d", s, got)
		}
	}
}

func TestGains(t *testing.T) {
	me := Vec3{}
	vol := func(d float32) float64 { l, r := Gains(me, Vec3{Z: d}, 0, 1, 2, 40); return l + r }
	if !(vol(1) > vol(5) && vol(5) > vol(20) && vol(20) > vol(39) && vol(39) > 0) {
		t.Fatal("volume must fall off with distance")
	}
	if vol(40) != 0 || vol(100) != 0 {
		t.Fatal("must be silent beyond max range")
	}
	// Facing +Z, a speaker on the listener's right-hand side is louder in the right ear.
	l, r := Gains(me, Vec3{X: 5}, 0, 1, 2, 40)
	l2, r2 := Gains(me, Vec3{X: -5}, 0, 1, 2, 40)
	if (r > l) == (r2 > l2) || l == r {
		t.Fatalf("no stereo separation: %v %v / %v %v", l, r, l2, r2)
	}
}

func TestGate(t *testing.T) {
	g := Gate{Threshold: 300, Hangover: 2}
	loud, quiet := make([]int16, FrameSamples), make([]int16, FrameSamples)
	for i := range loud {
		loud[i] = 5000
	}
	if g.Open(quiet) || !g.Open(loud) || !g.Open(quiet) || !g.Open(quiet) || g.Open(quiet) {
		t.Fatal("gate/hangover behaviour wrong")
	}
}
