package server

import (
	"net"
	"testing"
	"time"

	"github.com/fsminecrafter/sims3mp/internal/proto"
)

func hello(t *testing.T, addr, name, secret, pass string) (net.Conn, uint8, proto.R) {
	c, err := net.Dial("tcp", addr)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.Close() })
	c.Write(proto.Frame(proto.CHello, (&proto.W{}).U16(proto.Version).Str(name).Str(secret).Str(pass).B))
	c.SetReadDeadline(time.Now().Add(3 * time.Second))
	typ, p, err := proto.ReadFrame(c)
	if err != nil {
		t.Fatal(err)
	}
	return c, typ, proto.R{B: p}
}

func TestIdentityAndPersistence(t *testing.T) {
	dir := t.TempDir()
	s, err := New(Config{Addr: "127.0.0.1:0", DataDir: dir, AdminPassword: "pw", JoinPassword: "join", Logf: t.Logf})
	if err != nil {
		t.Fatal(err)
	}
	go s.Serve()

	if _, typ, _ := hello(t, s.Addr(), "Ann", "secret-one", "nope"); typ != proto.SReject {
		t.Fatal("wrong join password accepted")
	}
	c, typ, r := hello(t, s.Addr(), "Ann", "secret-one", "join")
	if typ != proto.SWelcome {
		t.Fatal("not welcomed")
	}
	if r.U32(); r.Bool() {
		t.Fatal("join password must not grant admin")
	}
	// Path traversal in an upload is refused (and uploads are admin-only anyway).
	c.Write(proto.Frame(proto.CClaimLot, (&proto.W{}).U64(5).B))
	c.Close()
	time.Sleep(100 * time.Millisecond)
	if _, typ, _ := hello(t, s.Addr(), "Ann", "someone-else", "join"); typ != proto.SReject {
		t.Fatal("name taken over with a different identity")
	}
	a, typ, _ := hello(t, s.Addr(), "Root", "admin-secret", "pw")
	if typ != proto.SWelcome {
		t.Fatal("admin not welcomed")
	}
	a.Write(proto.Frame(proto.CUploadBegin, []byte{proto.KindWorld}))
	a.Write(proto.Frame(proto.CFileBegin, (&proto.W{}).U8(proto.KindWorld).Str("../../evil").U64(1).Raw(make([]byte, 32)).B))
	a.SetReadDeadline(time.Now().Add(3 * time.Second))
	for {
		if _, _, err := proto.ReadFrame(a); err != nil {
			break // dropped, as it should be
		}
	}
	s.Close()

	s2, err := New(Config{Addr: "127.0.0.1:0", DataDir: dir, AdminPassword: "pw", Logf: t.Logf})
	if err != nil {
		t.Fatal(err)
	}
	defer s2.Close()
	if l := s2.st.Lots[5]; l == nil || l.Owner != "Ann" {
		t.Fatalf("lot ownership not persisted: %+v", s2.st.Lots)
	}
}

// The server's share of the latency budget: time from a client sending an
// operation to every client having it, over loopback.
func TestRelayLatency(t *testing.T) {
	s, err := New(Config{Addr: "127.0.0.1:0", DataDir: t.TempDir(), AdminPassword: "pw", Logf: func(string, ...any) {}})
	if err != nil {
		t.Fatal(err)
	}
	go s.Serve()
	defer s.Close()
	s.mu.Lock()
	s.state, s.st.HasWorld = proto.StateRunning, true
	s.mu.Unlock()
	a, _, _ := hello(t, s.Addr(), "A", "secret-aaaa", "pw")
	b, _, _ := hello(t, s.Addr(), "B", "secret-bbbb", "")
	a.Write(proto.Frame(proto.CReady, nil))
	b.Write(proto.Frame(proto.CReady, nil))
	until(t, a, proto.SSnapshotEnd)
	until(t, b, proto.SSnapshotEnd)
	b.SetReadDeadline(time.Now().Add(20 * time.Second))
	go func() { // drain A
		for {
			if _, _, err := proto.ReadFrame(a); err != nil {
				return
			}
		}
	}()
	const n = 2000
	var worst, total time.Duration
	for i := 0; i < n; i++ {
		start := time.Now()
		a.Write(proto.Frame(proto.COp, (&proto.W{}).U8(proto.OpWorld).U64(0).U64(0).Bytes(make([]byte, 64)).B))
		for {
			typ, _, err := proto.ReadFrame(b)
			if err != nil {
				t.Fatal(err)
			}
			if typ == proto.SOp {
				break
			}
		}
		d := time.Since(start)
		total += d
		if d > worst {
			worst = d
		}
	}
	t.Logf("op relay over loopback: mean %v, worst %v", total/n, worst)
	if total/n > 5*time.Millisecond {
		t.Fatalf("relay too slow: mean %v", total/n)
	}
}

// until reads frames until one of the wanted type arrives.
func until(t *testing.T, c net.Conn, typ uint8) proto.R {
	t.Helper()
	c.SetReadDeadline(time.Now().Add(3 * time.Second))
	for {
		got, p, err := proto.ReadFrame(c)
		if err != nil {
			t.Fatalf("waiting for message %d: %v", typ, err)
		}
		if got == typ {
			return proto.R{B: p}
		}
	}
}

func running(t *testing.T, dir string) *Server {
	s, err := New(Config{Addr: "127.0.0.1:0", DataDir: dir, AdminPassword: "pw", Logf: func(string, ...any) {}})
	if err != nil {
		t.Fatal(err)
	}
	go s.Serve()
	s.mu.Lock()
	s.state, s.st.HasWorld, s.dirty = proto.StateRunning, true, true
	s.mu.Unlock()
	return s
}

func op(cat uint8, lot uint64, tag string) []byte {
	return proto.Frame(proto.COp, (&proto.W{}).U8(cat).U64(lot).U64(0).Bytes([]byte(tag)).B)
}

func TestOpLogAndEntityData(t *testing.T) {
	dir := t.TempDir()
	s := running(t, dir)
	a, _, _ := hello(t, s.Addr(), "Ann", "secret-ann", "")
	b, _, _ := hello(t, s.Addr(), "Ben", "secret-ben", "")
	for _, c := range []net.Conn{a, b} {
		c.Write(proto.Frame(proto.CReady, nil))
		until(t, c, proto.SSnapshotEnd)
	}

	// Needs and money: only the owner of a household may publish its data.
	const house = 42
	vitals := func(v string) []byte {
		return proto.Frame(proto.CEntityData, (&proto.W{}).U64(house).U8(1).U64(7).Bytes([]byte(v)).B)
	}
	b.Write(vitals("forged")) // Ben does not own it: ignored
	a.Write(proto.Frame(proto.CClaimEntity, (&proto.W{}).U64(house).B))
	a.Write(vitals("funds=1500"))
	r := until(t, b, proto.SEntityData)
	if r.U64() != house || r.U8() != 1 || r.U64() != 7 || string(r.Bytes()) != "funds=1500" {
		t.Fatal("wrong entity data relayed")
	}

	// Lot structure: only someone allowed to build there may publish it.
	const lot = 9
	structure := proto.Frame(proto.CLotData, (&proto.W{}).U64(lot).U8(1).Bytes([]byte("walls-v1")).B)
	b.Write(structure)
	until(t, b, proto.SOpRejected)
	a.Write(proto.Frame(proto.CClaimLot, (&proto.W{}).U64(lot).B))
	a.Write(structure)
	r = until(t, b, proto.SLotData)
	if r.U64() != lot || r.U8() != 1 || string(r.Bytes()) != "walls-v1" {
		t.Fatal("wrong lot data relayed")
	}

	// A transient op is relayed but not recorded; world ops are recorded.
	a.Write(op(proto.OpWorld, 0, "one"))
	a.Write(op(proto.OpWorld|proto.OpTransient, 0, "fleeting"))
	a.Write(op(proto.OpWorld, 0, "two"))
	var seqs []uint64
	for len(seqs) < 3 {
		r := until(t, b, proto.SOp)
		seqs = append(seqs, r.U64())
	}

	// Restart the server: log and entity data must survive.
	s.Close()
	s = running(t, dir)
	defer s.Close()
	c, _, _ := hello(t, s.Addr(), "Cat", "secret-cat", "")
	c.Write(proto.Frame(proto.CReady, nil))
	r = until(t, c, proto.SEntityData)
	if r.U64(); r.U8() != 1 || r.U64() != 7 || string(r.Bytes()) != "funds=1500" {
		t.Fatal("entity data lost over restart")
	}
	r = until(t, c, proto.SLotData)
	if r.U64() != lot || r.U8() != 1 || string(r.Bytes()) != "walls-v1" {
		t.Fatal("lot data lost over restart")
	}
	var tags []string
	c.SetReadDeadline(time.Now().Add(3 * time.Second))
	for {
		typ, p, err := proto.ReadFrame(c)
		if err != nil {
			t.Fatal(err)
		}
		if typ == proto.SSnapshotEnd {
			break
		}
		if typ == proto.SOp {
			r := proto.R{B: p[29:]}
			tags = append(tags, string(r.Bytes()))
		}
	}
	if len(tags) != 2 || tags[0] != "one" || tags[1] != "two" {
		t.Fatalf("replayed ops = %v, want [one two]", tags)
	}

	// Resuming after the first op replays only what came later, and new ops
	// continue the old numbering.
	d, _, _ := hello(t, s.Addr(), "Dan", "secret-dan", "")
	d.Write(proto.Frame(proto.CReady, (&proto.W{}).U64(seqs[0]).B))
	r = until(t, d, proto.SOp)
	if got := r.U64(); got != seqs[2] {
		t.Fatalf("resume replayed seq %d, want %d", got, seqs[2])
	}
	until(t, d, proto.SSnapshotEnd)
	d.Write(op(proto.OpWorld, 0, "three"))
	r = until(t, d, proto.SOp)
	if got := r.U64(); got <= seqs[2] {
		t.Fatalf("sequence went backwards after restart: %d", got)
	}
}
