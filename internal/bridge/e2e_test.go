package bridge

import (
	"bytes"
	"net"
	"os"
	"path/filepath"
	"sync"
	"testing"
	"time"

	"github.com/fsminecrafter/sims3mp/internal/proto"
	"github.com/fsminecrafter/sims3mp/internal/server"
	"github.com/fsminecrafter/sims3mp/internal/voice"
)

// script plays the role of the in-game script mod talking to a bridge.
type script struct {
	t    *testing.T
	name string
	c    net.Conn
	in   chan [2][]byte
}

func dialScript(t *testing.T, name string, b *Bridge) *script {
	c, err := net.Dial("tcp", b.LocalAddr())
	if err != nil {
		t.Fatal(err)
	}
	s := &script{t: t, name: name, c: c, in: make(chan [2][]byte, 4096)}
	go func() {
		for {
			typ, p, err := proto.ReadFrame(c)
			if err != nil {
				close(s.in)
				return
			}
			s.in <- [2][]byte{{typ}, p}
		}
	}()
	return s
}

func (s *script) send(typ uint8, w *proto.W) {
	if w == nil {
		w = &proto.W{}
	}
	if _, err := s.c.Write(proto.Frame(typ, w.B)); err != nil {
		s.t.Fatal(err)
	}
}

// expect waits for a frame of the given type for which ok returns true.
func (s *script) expect(what string, typ uint8, ok func(r *proto.R) bool) {
	s.t.Helper()
	deadline := time.After(5 * time.Second)
	for {
		select {
		case f, open := <-s.in:
			if !open {
				s.t.Fatalf("%s: connection closed waiting for %s", s.name, what)
			}
			if f[0][0] == typ && (ok == nil || ok(&proto.R{B: f[1]})) {
				return
			}
		case <-deadline:
			s.t.Fatalf("%s: timed out waiting for %s", s.name, what)
		}
	}
}

func (s *script) phase(p uint8) {
	s.t.Helper()
	s.expect("phase", proto.LStatus, func(r *proto.R) bool {
		got := r.U8()
		if got == proto.PhaseError && p != proto.PhaseError {
			s.t.Fatalf("%s: bridge error: %s", s.name, r.Str())
		}
		return got == p
	})
}

func (s *script) never(what string, typ uint8, d time.Duration) {
	s.t.Helper()
	deadline := time.After(d)
	for {
		select {
		case f := <-s.in:
			if f[0] != nil && f[0][0] == typ {
				s.t.Fatalf("%s: unexpected %s", s.name, what)
			}
		case <-deadline:
			return
		}
	}
}

type fakeAudio struct {
	mu     sync.Mutex
	talk   bool
	heard  float64 // peak output level
	closed chan struct{}
}

func (a *fakeAudio) Read(f []int16) error {
	time.Sleep(5 * time.Millisecond)
	a.mu.Lock()
	defer a.mu.Unlock()
	for i := range f {
		f[i] = 0
		if a.talk {
			f[i] = int16(8000 * (i%32 - 16) / 16)
		}
	}
	return nil
}
func (a *fakeAudio) Write(o []int16) error {
	time.Sleep(5 * time.Millisecond)
	a.mu.Lock()
	defer a.mu.Unlock()
	for _, s := range o {
		if v := float64(s); v > a.heard {
			a.heard = v
		}
	}
	return nil
}
func (a *fakeAudio) Close() {}
func (a *fakeAudio) peak(reset bool) float64 {
	a.mu.Lock()
	defer a.mu.Unlock()
	v := a.heard
	if reset {
		a.heard = 0
	}
	return v
}

func write(t *testing.T, path, data string) {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(data), 0o644); err != nil {
		t.Fatal(err)
	}
}

func newBridge(t *testing.T, ts3 string, a *fakeAudio) *Bridge {
	b, err := New(Config{LocalAddr: "127.0.0.1:0", TS3Dir: ts3, Logf: t.Logf,
		Audio: func() (voice.Device, error) { return a, nil }})
	if err != nil {
		t.Fatal(err)
	}
	go b.Run()
	t.Cleanup(b.Close)
	return b
}

func TestSession(t *testing.T) {
	srv, err := server.New(server.Config{Addr: "127.0.0.1:0", DataDir: t.TempDir(), AdminPassword: "letmein", Logf: t.Logf})
	if err != nil {
		t.Fatal(err)
	}
	go srv.Serve()
	t.Cleanup(srv.Close)

	// The admin has the game, a save and two mods. Bob has none of them.
	adminDir, bobDir := t.TempDir(), t.TempDir()
	big := string(bytes.Repeat([]byte("sims"), 100000)) // spans several chunks
	write(t, filepath.Join(adminDir, "Saves", "Town.sims3", "Meta.data"), "meta")
	write(t, filepath.Join(adminDir, "Saves", "Town.sims3", "Sunset Valley_0x0859db4c.nhd"), big)
	write(t, filepath.Join(adminDir, "Mods", "Packages", "Sims3MP.package"), "mp-mod")
	write(t, filepath.Join(adminDir, "Mods", "Packages", "NRaas", "MasterController.package"), "nraas")
	write(t, filepath.Join(adminDir, "Mods", "Packages", "notes.txt"), "not a package: must not be distributed")
	write(t, filepath.Join(bobDir, "Mods", "Packages", "Sims3MP.package"), "mp-mod")

	aAudio, bAudio := &fakeAudio{}, &fakeAudio{}
	ab, bb := newBridge(t, adminDir, aAudio), newBridge(t, bobDir, bAudio)
	admin, bob := dialScript(t, "admin", ab), dialScript(t, "bob", bb)

	// Bob joins before there is a world: he waits.
	bob.send(proto.LConnect, (&proto.W{}).Str(srv.Addr()).Str("Bob").Str(""))
	bob.phase(proto.PhaseWaitingForWorld)

	// Admin connects and hosts; server goes initialising -> running.
	admin.send(proto.LConnect, (&proto.W{}).Str(srv.Addr()).Str("Alice").Str("letmein"))
	admin.expect("welcome as admin", proto.SWelcome, func(r *proto.R) bool { r.U32(); return r.Bool() })
	admin.send(proto.LHostWorld, (&proto.W{}).Str("Town"))
	bob.expect("initialising", proto.SState, func(r *proto.R) bool { return r.U8() == proto.StateInitializing })
	admin.phase(proto.PhaseWorldReady)

	// Bob is missing a mod: it is installed and he is told to restart.
	bob.phase(proto.PhaseRestartRequired)
	if d, _ := os.ReadFile(filepath.Join(bobDir, "Mods", "Packages", "NRaas", "MasterController.package")); string(d) != "nraas" {
		t.Fatalf("mod not installed, got %q", d)
	}
	if _, err := os.Stat(filepath.Join(bobDir, "Mods", "Packages", "notes.txt")); err == nil {
		t.Fatal("non-package file was distributed")
	}

	// "Restart": reconnect. Now the world downloads.
	bob.send(proto.LConnect, (&proto.W{}).Str(srv.Addr()).Str("Bob").Str(""))
	bob.phase(proto.PhaseWorldReady)
	matches, _ := filepath.Glob(filepath.Join(bobDir, "Saves", "Sims3MP_*.sims3", "*.nhd"))
	if len(matches) != 1 {
		t.Fatalf("world not downloaded: %v", matches)
	}
	if d, _ := os.ReadFile(matches[0]); string(d) != big {
		t.Fatal("downloaded world differs")
	}

	// Both enter the world.
	admin.send(proto.CReady, nil)
	admin.expect("snapshot", proto.SSnapshotEnd, nil)
	admin.expect("clock 1x", proto.SClock, func(r *proto.R) bool { r.U64(); return r.U8() == 1 })
	bob.send(proto.CReady, nil)
	bob.expect("snapshot", proto.SSnapshotEnd, nil)

	// Households: each player claims their own; nobody can take another's.
	const aliceHouse, bobHouse, lotA = 1001, 1002, 77
	admin.send(proto.CClaimEntity, (&proto.W{}).U64(aliceHouse))
	bob.send(proto.CClaimEntity, (&proto.W{}).U64(bobHouse))
	bob.expect("bob owns his household", proto.SEntity, func(r *proto.R) bool { return r.U64() == bobHouse && r.Str() == "Bob" })
	bob.send(proto.CClaimEntity, (&proto.W{}).U64(aliceHouse))
	bob.expect("claim refused", proto.SNotice, nil)

	// Lots: Alice buys lot 77. Bob cannot build there until made a builder.
	admin.send(proto.CClaimLot, (&proto.W{}).U64(lotA))
	bob.expect("lot owner broadcast", proto.SLot, func(r *proto.R) bool { return r.U64() == lotA && r.Str() == "Alice" })
	build := func() *proto.W { return (&proto.W{}).U8(proto.OpBuild).U64(lotA).U64(0).Bytes([]byte("wall")) }
	bob.send(proto.COp, build())
	bob.expect("build rejected", proto.SOpRejected, nil)
	admin.send(proto.CSetBuilder, (&proto.W{}).U64(lotA).Str("Bob").Bool(true))
	bob.expect("builder added", proto.SLot, func(r *proto.R) bool { r.U64(); r.Str(); return r.U16() == 1 && r.Str() == "Bob" })
	bob.send(proto.COp, build())
	admin.expect("build op relayed", proto.SOp, func(r *proto.R) bool {
		r.U64()
		r.U32()
		return r.U8() == proto.OpBuild && r.U64() == lotA && r.U64() == 0 && string(r.Bytes()) == "wall"
	})

	// Only the admin may send admin ops (NRaas etc.) or change the speed.
	bob.send(proto.COp, (&proto.W{}).U8(proto.OpAdmin).U64(0).U64(0).Bytes(nil))
	bob.expect("admin op rejected", proto.SOpRejected, nil)
	bob.send(proto.CSetSpeed, (&proto.W{}).U8(3))
	bob.expect("speed change refused", proto.SNotice, nil)

	// Time: 3x only while everybody sleeps.
	speed := func(want uint8) func(r *proto.R) bool {
		return func(r *proto.R) bool { r.U64(); return r.U8() == want }
	}
	bob.send(proto.CSetSleeping, (&proto.W{}).Bool(true))
	bob.never("speed-up with someone awake", proto.SClock, 300*time.Millisecond)
	admin.send(proto.CSetSleeping, (&proto.W{}).Bool(true))
	bob.expect("3x", proto.SClock, speed(3))
	admin.send(proto.CSetSleeping, (&proto.W{}).Bool(false))
	bob.expect("back to 1x", proto.SClock, speed(1))
	admin.send(proto.CSetSpeed, (&proto.W{}).U8(2))
	bob.expect("admin sets 2x", proto.SClock, speed(2))
	admin.send(proto.CSetSpeed, (&proto.W{}).U8(proto.SpeedAuto))
	bob.expect("auto again", proto.SClock, speed(1))

	// Realtime sim state over UDP: relayed for owned households only.
	state := func(ent uint64, tag string) *proto.W { return (&proto.W{}).U64(ent).Raw([]byte(tag)) }
	deadline := time.Now().Add(3 * time.Second)
	got := false
	for !got && time.Now().Before(deadline) {
		bob.send(proto.LState, state(aliceHouse, "forged"))
		bob.send(proto.LState, state(bobHouse, "walk"))
		admin.send(proto.LListener, (&proto.W{}).F32(0).F32(0).F32(0).F32(0).F32(1)) // registers UDP path
		select {
		case f := <-admin.in:
			if f[0][0] == proto.LStateRelay {
				r := proto.R{B: f[1]}
				r.U32()
				if ent := r.U64(); ent != bobHouse || string(r.Rest()) != "walk" {
					t.Fatalf("bad state relay for entity %d", ent)
				}
				got = true
			}
		case <-time.After(20 * time.Millisecond):
		}
	}
	if !got {
		t.Fatal("state was not relayed over UDP")
	}

	// Voice: Bob talks next to Alice -> she hears him; far away -> silence.
	listen := func(s *script, x float32) { s.send(proto.LListener, (&proto.W{}).F32(x).F32(0).F32(0).F32(0).F32(1)) }
	listen(admin, 0)
	listen(bob, 3)
	time.Sleep(150 * time.Millisecond)
	bAudio.mu.Lock()
	bAudio.talk = true
	bAudio.mu.Unlock()
	time.Sleep(600 * time.Millisecond)
	if p := aAudio.peak(true); p < 500 {
		t.Fatalf("nearby voice not heard (peak %.0f)", p)
	}
	time.Sleep(150 * time.Millisecond)
	listen(bob, 500)
	time.Sleep(400 * time.Millisecond)
	aAudio.peak(true)
	time.Sleep(400 * time.Millisecond)
	if p := aAudio.peak(false); p != 0 {
		t.Fatalf("distant voice still audible (peak %.0f)", p)
	}

	// A late joiner's snapshot contains ownership and the op backlog.
	bob.send(proto.CReady, nil)
	bob.expect("lot in snapshot", proto.SLot, func(r *proto.R) bool { return r.U64() == lotA })
	bob.expect("op backlog", proto.SOp, nil)
	bob.expect("snapshot end", proto.SSnapshotEnd, nil)
}
