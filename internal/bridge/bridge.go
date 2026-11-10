// Package bridge is the client-side companion process. The Sims 3 script
// runtime has no sockets, threads or file access, so the script mod talks to
// this process over a loopback connection (through a tiny native shim) and
// the bridge does everything the game cannot: the server connection, the UDP
// realtime channel, world/mod file transfer and voice chat.
package bridge

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"log"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/fsminecrafter/sims3mp/internal/proto"
	"github.com/fsminecrafter/sims3mp/internal/voice"
	"github.com/fsminecrafter/sims3mp/internal/xfer"
)

type Config struct {
	LocalAddr string // loopback address the script mod connects to
	TS3Dir    string // ...\Documents\Electronic Arts\The Sims 3
	ServerExe string // path of sims3mp-server, for "host on this PC"
	Logf      func(string, ...any)
	Audio     func() (voice.Device, error) // nil disables voice
}

type Bridge struct {
	cfg Config
	ln  net.Listener

	mu       sync.Mutex
	script   net.Conn
	srv      net.Conn
	srvW     sync.Mutex
	udp      *net.UDPConn
	gen      int // bumped on every (dis)connect so stale goroutines stop
	phase    uint8
	text     string
	playerID uint32
	admin    bool
	udpKey   uint64
	saveName string
	worldDir string // where the world of this session lives locally
	hosted   bool
	lists    [2][]proto.FileEntry
	haveList [2]bool
	recv     xfer.Receiver
	fileDone chan error
	child    *exec.Cmd
	rtt      atomic.Int64

	listener   voice.Vec3
	mic, spk   atomic.Bool
	inSession  atomic.Bool
	mixer      *voice.Mixer
	lastPosOut time.Time
}

func New(cfg Config) (*Bridge, error) {
	if cfg.Logf == nil {
		cfg.Logf = log.Printf
	}
	ln, err := net.Listen("tcp", cfg.LocalAddr)
	if err != nil {
		return nil, err
	}
	b := &Bridge{cfg: cfg, ln: ln, mixer: voice.NewMixer(2, 40), text: "Not connected"}
	b.mic.Store(true)
	b.spk.Store(true)
	return b, nil
}

func (b *Bridge) LocalAddr() string { return b.ln.Addr().String() }

func (b *Bridge) modsDir() string  { return filepath.Join(b.cfg.TS3Dir, "Mods", "Packages") }
func (b *Bridge) savesDir() string { return filepath.Join(b.cfg.TS3Dir, "Saves") }
func (b *Bridge) ownDir() string   { return filepath.Join(b.cfg.TS3Dir, "Sims3MP") }

// Run serves script connections until the listener is closed.
func (b *Bridge) Run() error {
	if b.cfg.Audio != nil {
		go b.audioLoop()
	}
	for {
		c, err := b.ln.Accept()
		if err != nil {
			return err
		}
		b.mu.Lock()
		if b.script != nil {
			b.script.Close() // the game restarted or reloaded: newest wins
		}
		b.script = c
		b.mu.Unlock()
		go b.scriptLoop(c)
	}
}

func (b *Bridge) Close() {
	b.ln.Close()
	b.disconnect("Bridge closed")
	b.mu.Lock()
	if b.child != nil && b.child.Process != nil {
		b.child.Process.Kill()
	}
	b.mu.Unlock()
}

// --- script side ---------------------------------------------------------

func (b *Bridge) toScript(frame []byte) {
	b.mu.Lock()
	c := b.script
	b.mu.Unlock()
	if c != nil {
		c.SetWriteDeadline(time.Now().Add(5 * time.Second))
		if _, err := c.Write(frame); err != nil {
			c.Close()
		}
	}
}

func (b *Bridge) statusFrame() []byte {
	b.mu.Lock()
	defer b.mu.Unlock()
	return proto.Frame(proto.LStatus, (&proto.W{}).U8(b.phase).Str(b.text).U32(b.playerID).Bool(b.admin).Str(b.saveName).B)
}

func (b *Bridge) setStatus(phase uint8, format string, args ...any) {
	b.mu.Lock()
	b.phase, b.text = phase, fmt.Sprintf(format, args...)
	b.mu.Unlock()
	b.cfg.Logf("status: %s", b.text)
	b.toScript(b.statusFrame())
}

func (b *Bridge) scriptLoop(c net.Conn) {
	defer func() {
		c.Close()
		b.mu.Lock()
		if b.script == c {
			b.script = nil
			b.inSession.Store(false)
		}
		b.mu.Unlock()
	}()
	b.toScript(b.statusFrame())
	for {
		typ, p, err := proto.ReadFrame(c)
		if err != nil {
			return
		}
		r := proto.R{B: p}
		switch typ {
		case proto.LConnect:
			addr, name, pass := r.Str(), r.Str(), r.Str()
			if !r.Err {
				go b.connect(addr, name, pass)
			}
		case proto.LStartServer:
			port, name := r.U16(), r.Str()
			if !r.Err {
				go b.startServer(port, name)
			}
		case proto.LDisconnect:
			b.disconnect("Disconnected")
		case proto.LHostWorld:
			save := r.Str()
			if !r.Err {
				go b.hostWorld(save)
			}
		case proto.LQuery:
			b.toScript(b.statusFrame())
		case proto.LState:
			b.sendUDP(proto.UState, p)
		case proto.LListener:
			pos := voice.Vec3{X: r.F32(), Y: r.F32(), Z: r.F32()}
			fx, fz := r.F32(), r.F32()
			if r.Err {
				continue
			}
			b.mixer.SetListener(pos, fx, fz)
			b.mu.Lock()
			b.listener = pos
			due := time.Since(b.lastPosOut) > 100*time.Millisecond
			if due {
				b.lastPosOut = time.Now()
			}
			b.mu.Unlock()
			if due {
				b.sendUDP(proto.UPosition, (&proto.W{}).F32(pos.X).F32(pos.Y).F32(pos.Z).B)
			}
		case proto.LVoice:
			b.mic.Store(r.Bool())
			b.spk.Store(r.Bool())
		default:
			if typ >= 64 {
				continue // not a client->server message
			}
			if typ == proto.CReady {
				b.inSession.Store(true)
			}
			b.toServer(proto.Frame(typ, p))
		}
	}
}

// --- server side ---------------------------------------------------------

func (b *Bridge) toServer(frame []byte) error {
	b.mu.Lock()
	c := b.srv
	b.mu.Unlock()
	if c == nil {
		return errors.New("not connected")
	}
	b.srvW.Lock()
	defer b.srvW.Unlock()
	c.SetWriteDeadline(time.Now().Add(30 * time.Second))
	_, err := c.Write(frame)
	return err
}

func (b *Bridge) sendUDP(typ uint8, payload []byte) {
	b.mu.Lock()
	u, key := b.udp, b.udpKey
	b.mu.Unlock()
	if u != nil {
		u.Write(proto.Datagram(typ, key, payload))
	}
}

func (b *Bridge) identity() (string, error) {
	p := filepath.Join(b.ownDir(), "identity.key")
	if d, err := os.ReadFile(p); err == nil && len(d) >= 16 {
		return strings.TrimSpace(string(d)), nil
	}
	var k [16]byte
	rand.Read(k[:])
	s := hex.EncodeToString(k[:])
	if err := os.MkdirAll(b.ownDir(), 0o755); err != nil {
		return "", err
	}
	return s, os.WriteFile(p, []byte(s), 0o600)
}

func (b *Bridge) disconnect(why string) {
	b.mu.Lock()
	b.gen++
	if b.srv != nil {
		b.srv.Close()
		b.srv = nil
	}
	if b.udp != nil {
		b.udp.Close()
		b.udp = nil
	}
	b.playerID, b.admin, b.saveName, b.hosted = 0, false, "", false
	b.haveList = [2]bool{}
	b.inSession.Store(false)
	b.mu.Unlock()
	b.setStatus(proto.PhaseIdle, "%s", why)
}

func (b *Bridge) connect(addr, name, pass string) {
	b.disconnect("Connecting...")
	if !strings.Contains(addr, ":") {
		addr += ":38920"
	}
	b.setStatus(proto.PhaseConnecting, "Connecting to %s...", addr)
	fail := func(err error) { b.setStatus(proto.PhaseError, "Could not connect: %v", err) }
	secret, err := b.identity()
	if err != nil {
		fail(err)
		return
	}
	c, err := net.DialTimeout("tcp", addr, 8*time.Second)
	if err != nil {
		fail(err)
		return
	}
	c.(*net.TCPConn).SetNoDelay(true)
	c.Write(proto.Frame(proto.CHello, (&proto.W{}).U16(proto.Version).Str(name).Str(secret).Str(pass).B))
	c.SetReadDeadline(time.Now().Add(10 * time.Second))
	typ, p, err := proto.ReadFrame(c)
	if err != nil {
		c.Close()
		fail(err)
		return
	}
	r := proto.R{B: p}
	if typ != proto.SWelcome {
		c.Close()
		fail(errors.New(r.Str()))
		return
	}
	id, admin, key, state := r.U32(), r.Bool(), r.U64(), r.U8()
	ua, err := net.ResolveUDPAddr("udp", addr)
	var u *net.UDPConn
	if err == nil {
		u, err = net.DialUDP("udp", nil, ua)
	}
	if err != nil {
		c.Close()
		fail(err)
		return
	}
	b.mu.Lock()
	b.gen++
	gen := b.gen
	b.srv, b.udp, b.playerID, b.admin, b.udpKey = c, u, id, admin, key
	// Non-hosts keep one download folder per server so rejoining is cheap.
	safe := strings.NewReplacer(":", "_", ".", "_", "[", "", "]", "").Replace(addr)
	b.saveName = "Sims3MP_" + safe
	b.worldDir = filepath.Join(b.savesDir(), b.saveName+".sims3")
	b.mu.Unlock()
	b.toScript(proto.Frame(typ, p))
	if state == proto.StateRunning {
		b.setStatus(proto.PhaseSyncing, "Connected, checking files...")
	} else if admin {
		b.setStatus(proto.PhaseWaitingForWorld, "Connected as admin. The server has no world yet: use 'Copy world and start hosting'.")
	} else {
		b.setStatus(proto.PhaseWaitingForWorld, "Connected. Waiting for the admin to start the world...")
	}
	go b.udpLoop(u, gen)
	go b.keepalive(gen)
	b.serverLoop(c, gen)
}

func (b *Bridge) alive(gen int) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.gen == gen
}

func (b *Bridge) keepalive(gen int) {
	for b.alive(gen) {
		b.toServer(proto.Frame(proto.CPing, (&proto.W{}).U64(uint64(time.Now().UnixMicro())).B))
		b.mu.Lock()
		p := b.listener
		b.mu.Unlock()
		b.sendUDP(proto.UPosition, (&proto.W{}).F32(p.X).F32(p.Y).F32(p.Z).B)
		time.Sleep(3 * time.Second)
	}
}

func (b *Bridge) serverLoop(c net.Conn, gen int) {
	for {
		c.SetReadDeadline(time.Now().Add(60 * time.Second))
		typ, p, err := proto.ReadFrame(c)
		if err != nil {
			if b.alive(gen) {
				b.disconnect("Connection to the server was lost")
			}
			return
		}
		r := proto.R{B: p}
		switch typ {
		case proto.SFileList:
			kind := r.U8()
			r.U32()
			n := r.U32()
			if kind > 1 || n > 100000 {
				continue
			}
			l := make([]proto.FileEntry, 0, n)
			for i := uint32(0); i < n && !r.Err; i++ {
				e := proto.FileEntry{Path: r.Str(), Size: r.U64()}
				copy(e.Sum[:], r.Raw(32))
				l = append(l, e)
			}
			if r.Err {
				continue
			}
			b.mu.Lock()
			b.lists[kind], b.haveList[kind] = l, true
			both := b.haveList[0] && b.haveList[1]
			if both {
				b.haveList = [2]bool{}
			}
			b.mu.Unlock()
			if both {
				go b.syncFiles(gen)
			}
		case proto.CFileBegin:
			_, err := b.recv.Begin(p, func(kind uint8) (string, bool) {
				if kind == proto.KindMods {
					return b.modsDir() + ".sims3mp-incoming", true
				}
				return b.worldDir, kind == proto.KindWorld
			})
			if err != nil {
				b.finishFile(err)
			}
		case proto.CFileChunk:
			if err := b.recv.Chunk(p); err != nil {
				b.finishFile(err)
			}
		case proto.CFileEnd:
			_, err := b.recv.End()
			b.finishFile(err)
		case proto.SPong:
			b.rtt.Store(time.Now().UnixMicro() - int64(r.U64()))
		case proto.SState:
			if st := r.U8(); st == proto.StateInitializing {
				b.inSession.Store(false)
			}
			b.toScript(proto.Frame(typ, p))
		default:
			b.toScript(proto.Frame(typ, p))
		}
	}
}

func (b *Bridge) finishFile(err error) {
	b.mu.Lock()
	ch := b.fileDone
	b.mu.Unlock()
	if ch != nil {
		select {
		case ch <- err:
		default:
		}
	}
}

func (b *Bridge) fetch(kind uint8, e proto.FileEntry) error {
	ch := make(chan error, 1)
	b.mu.Lock()
	b.fileDone = ch
	b.mu.Unlock()
	if err := b.toServer(proto.Frame(proto.CRequestFile, (&proto.W{}).U8(kind).Str(e.Path).B)); err != nil {
		return err
	}
	select {
	case err := <-ch:
		return err
	case <-time.After(10 * time.Minute):
		return errors.New("timed out downloading " + e.Path)
	}
}

func diff(want, have []proto.FileEntry) (missing []proto.FileEntry, extra []string) {
	m := map[string][32]byte{}
	for _, e := range have {
		m[e.Path] = e.Sum
	}
	for _, e := range want {
		if s, ok := m[e.Path]; !ok || s != e.Sum {
			missing = append(missing, e)
		}
		delete(m, e.Path)
	}
	for p := range m {
		extra = append(extra, p)
	}
	return
}

// syncFiles makes the local mods and world match the server. Mods come
// first: if any had to be installed the game must be restarted before the
// world can be loaded safely.
func (b *Bridge) syncFiles(gen int) {
	fail := func(err error) {
		if b.alive(gen) {
			b.setStatus(proto.PhaseError, "File sync failed: %v", err)
		}
	}
	b.mu.Lock()
	mods, world, worldDir, save := b.lists[proto.KindMods], b.lists[proto.KindWorld], b.worldDir, b.saveName
	b.mu.Unlock()

	have, err := xfer.Scan(b.modsDir())
	if err != nil {
		fail(err)
		return
	}
	var need []proto.FileEntry
	missing, _ := diff(mods, have)
	for _, e := range missing {
		// Only game packages are ever installed, whatever the server offers.
		if strings.HasSuffix(strings.ToLower(e.Path), ".package") {
			need = append(need, e)
		}
	}
	if len(need) > 0 {
		staging := b.modsDir() + ".sims3mp-incoming"
		os.RemoveAll(staging)
		for i, e := range need {
			b.setStatus(proto.PhaseSyncing, "Installing mod %d of %d: %s", i+1, len(need), e.Path)
			if err := b.fetch(proto.KindMods, e); err != nil {
				fail(err)
				return
			}
		}
		backup := filepath.Join(b.ownDir(), "replaced-mods", time.Now().Format("20060102-150405"))
		for _, e := range need {
			dst := filepath.Join(b.modsDir(), filepath.FromSlash(e.Path))
			if _, err := os.Stat(dst); err == nil {
				bk := filepath.Join(backup, filepath.FromSlash(e.Path))
				os.MkdirAll(filepath.Dir(bk), 0o755)
				os.Rename(dst, bk)
			}
			os.MkdirAll(filepath.Dir(dst), 0o755)
			if err := os.Rename(filepath.Join(staging, filepath.FromSlash(e.Path)), dst); err != nil {
				fail(err)
				return
			}
		}
		os.RemoveAll(staging)
		b.setStatus(proto.PhaseRestartRequired, "%d mod(s) required by this server were installed. Please restart The Sims 3, then join again.", len(need))
		return
	}

	have, err = xfer.Scan(worldDir)
	if err != nil {
		fail(err)
		return
	}
	missing, extra := diff(world, have)
	b.mu.Lock()
	hosted := b.hosted
	b.mu.Unlock()
	if hosted && len(missing) > 0 {
		// The admin's own save changed after it was uploaded (the game saved
		// again). Their copy stays the source of truth; never overwrite it.
		missing, extra = nil, nil
	}
	for i, e := range missing {
		b.setStatus(proto.PhaseSyncing, "Downloading world: file %d of %d", i+1, len(missing))
		if err := b.fetch(proto.KindWorld, e); err != nil {
			fail(err)
			return
		}
	}
	if !hosted {
		for _, p := range extra {
			os.Remove(filepath.Join(worldDir, filepath.FromSlash(p)))
		}
	}
	if b.alive(gen) {
		b.setStatus(proto.PhaseWorldReady, "World ready. Load the save '%s' and press F9 to enter the session.", save)
	}
}

// hostWorld uploads the admin's mods and save; the server pauses while the
// world is replaced and resumes when the upload is complete.
func (b *Bridge) hostWorld(save string) {
	fail := func(err error) { b.setStatus(proto.PhaseError, "Could not upload the world: %v", err) }
	name, ok := proto.CleanRel(strings.TrimSuffix(save, ".sims3"))
	if !ok || strings.Contains(name, "/") {
		fail(errors.New("invalid save name"))
		return
	}
	dir := filepath.Join(b.savesDir(), name+".sims3")
	world, err := xfer.Scan(dir)
	if err == nil && len(world) == 0 {
		err = fmt.Errorf("save '%s' was not found in %s (save the game first)", name, b.savesDir())
	}
	if err != nil {
		fail(err)
		return
	}
	mods, err := xfer.Scan(b.modsDir())
	if err != nil {
		fail(err)
		return
	}
	var pk []proto.FileEntry
	for _, e := range mods {
		if strings.HasSuffix(strings.ToLower(e.Path), ".package") {
			pk = append(pk, e)
		}
	}
	b.mu.Lock()
	b.worldDir, b.saveName, b.hosted = dir, name, true
	b.mu.Unlock()
	up := func(kind uint8, base string, list []proto.FileEntry, what string) error {
		if err := b.toServer(proto.Frame(proto.CUploadBegin, []byte{kind})); err != nil {
			return err
		}
		for i, e := range list {
			b.setStatus(proto.PhaseSyncing, "Uploading %s: file %d of %d", what, i+1, len(list))
			if err := xfer.Send(base, kind, e, b.toServer); err != nil {
				return err
			}
		}
		return b.toServer(proto.Frame(proto.CUploadEnd, []byte{kind}))
	}
	if err := up(proto.KindMods, b.modsDir(), pk, "mods"); err != nil {
		fail(err)
		return
	}
	if err := up(proto.KindWorld, dir, world, "world"); err != nil {
		fail(err)
		return
	}
	b.setStatus(proto.PhaseSyncing, "World uploaded, waiting for the server...")
}

// startServer runs a dedicated server on this PC and connects to it as admin.
func (b *Bridge) startServer(port uint16, name string) {
	b.mu.Lock()
	running := b.child != nil
	b.mu.Unlock()
	var kb [8]byte
	rand.Read(kb[:])
	pass := hex.EncodeToString(kb[:])
	pf := filepath.Join(b.ownDir(), "local-server-admin.key")
	if running {
		if d, err := os.ReadFile(pf); err == nil {
			pass = string(d)
		}
	} else {
		os.MkdirAll(b.ownDir(), 0o755)
		if err := os.WriteFile(pf, []byte(pass), 0o600); err != nil {
			b.setStatus(proto.PhaseError, "Could not start the server: %v", err)
			return
		}
		cmd := exec.Command(b.cfg.ServerExe, "-addr", fmt.Sprintf(":%d", port), "-data", filepath.Join(b.ownDir(), "server-data"))
		cmd.Env = append(os.Environ(), "SIMS3MP_ADMIN_PASSWORD="+pass)
		if lf, err := os.Create(filepath.Join(b.ownDir(), "server.log")); err == nil {
			cmd.Stdout, cmd.Stderr = lf, lf
		}
		if err := cmd.Start(); err != nil {
			b.setStatus(proto.PhaseError, "Could not start the server: %v", err)
			return
		}
		b.mu.Lock()
		b.child = cmd
		b.mu.Unlock()
		go func() {
			cmd.Wait()
			b.mu.Lock()
			b.child = nil
			b.mu.Unlock()
		}()
		time.Sleep(500 * time.Millisecond)
	}
	b.connect(fmt.Sprintf("127.0.0.1:%d", port), name, pass)
}

// --- realtime ------------------------------------------------------------

func (b *Bridge) udpLoop(u *net.UDPConn, gen int) {
	buf := make([]byte, 2048)
	for {
		n, err := u.Read(buf)
		if err != nil {
			if !b.alive(gen) {
				return
			}
			time.Sleep(50 * time.Millisecond) // e.g. ICMP unreachable while the server restarts
			continue
		}
		if n < 1 {
			continue
		}
		switch buf[0] {
		case proto.UStateRelay:
			b.toScript(proto.Frame(proto.LStateRelay, buf[1:n]))
		case proto.UVoiceRelay:
			r := proto.R{B: buf[1:n]}
			id, seq := r.U32(), r.U16()
			pos := voice.Vec3{X: r.F32(), Y: r.F32(), Z: r.F32()}
			if !r.Err && b.spk.Load() {
				b.mixer.Push(id, seq, pos, r.Rest())
			}
		}
	}
}

func (b *Bridge) audioLoop() {
	dev, err := b.cfg.Audio()
	if err != nil {
		b.cfg.Logf("voice chat disabled: %v", err)
		return
	}
	go func() { // playback: the device paces this loop at one frame per 20 ms
		out := make([]int16, 2*voice.FrameSamples)
		for {
			b.mixer.Mix(out)
			if dev.Write(out) != nil {
				return
			}
		}
	}()
	gate := voice.Gate{Threshold: 350, Hangover: 15}
	pcm := make([]int16, voice.FrameSamples)
	var seq uint16
	for {
		if err := dev.Read(pcm); err != nil {
			b.cfg.Logf("microphone unavailable: %v", err)
			return
		}
		if !gate.Open(pcm) || !b.mic.Load() || !b.inSession.Load() {
			continue
		}
		seq++
		b.mu.Lock()
		p := b.listener
		b.mu.Unlock()
		w := (&proto.W{}).U16(seq).F32(p.X).F32(p.Y).F32(p.Z)
		b.sendUDP(proto.UVoice, voice.Encode(w.B, pcm))
	}
}
