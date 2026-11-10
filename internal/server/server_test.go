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
