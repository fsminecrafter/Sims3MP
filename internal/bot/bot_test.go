package bot

import (
	"bytes"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/fsminecrafter/sims3mp/internal/proto"
	"github.com/fsminecrafter/sims3mp/internal/server"
)

type syncBuf struct {
	mu sync.Mutex
	b  bytes.Buffer
}

func (s *syncBuf) Write(p []byte) (int, error) { s.mu.Lock(); defer s.mu.Unlock(); return s.b.Write(p) }
func (s *syncBuf) String() string              { s.mu.Lock(); defer s.mu.Unlock(); return s.b.String() }

func waitFor(t *testing.T, out *syncBuf, want string) {
	t.Helper()
	for end := time.Now().Add(5 * time.Second); time.Now().Before(end); time.Sleep(10 * time.Millisecond) {
		if strings.Contains(out.String(), want) {
			return
		}
	}
	t.Fatalf("never saw %q in:\n%s", want, out.String())
}

func TestBotPlaysAgainstServer(t *testing.T) {
	srv, err := server.New(server.Config{Addr: "127.0.0.1:0", DataDir: t.TempDir(), AdminPassword: "pw", Logf: func(string, ...any) {}})
	if err != nil {
		t.Fatal(err)
	}
	go srv.Serve()
	defer srv.Close()

	out := &syncBuf{}
	b := New(Config{Server: srv.Addr(), Name: "Bot", IdentityFile: t.TempDir() + "/id"}, out)
	defer b.Close()
	if err := b.Connect(nil); err != nil {
		t.Fatal(err)
	}
	waitFor(t, out, "no world yet")

	// A second client (the "host") joins, then the bot's commands reach it.
	h, _, _ := hostConn(t, srv.Addr())
	waitFor(t, out, "Host joined (admin)")
	b.Command("chat hello from the bot")
	b.Command("claim-lot 42")
	b.Command("household 7")
	waitFor(t, out, "lot 42: owner Bot")
	waitFor(t, out, "household 7 is played by Bot")
	if !strings.Contains(readUntilChat(t, h), "hello from the bot") {
		t.Fatal("host did not receive the bot's chat")
	}
	b.Command("speed 3") // not admin: the server says no
	waitFor(t, out, "server: Only the admin")
}

func hostConn(t *testing.T, addr string) (net_ interface {
	Read([]byte) (int, error)
	Write([]byte) (int, error)
	Close() error
}, typ uint8, r proto.R) {
	t.Helper()
	c, err := dial(addr)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { c.Close() })
	c.Write(proto.Frame(proto.CHello, (&proto.W{}).U16(proto.Version).Str("Host").Str("host-secret-0123").Str("pw").B))
	typ, p, err := proto.ReadFrame(c)
	if err != nil {
		t.Fatal(err)
	}
	return c, typ, proto.R{B: p}
}

func readUntilChat(t *testing.T, c interface{ Read([]byte) (int, error) }) string {
	t.Helper()
	for {
		typ, p, err := proto.ReadFrame(c)
		if err != nil {
			t.Fatal(err)
		}
		if typ == proto.SChat {
			r := proto.R{B: p}
			r.U32()
			return r.Str()
		}
	}
}
