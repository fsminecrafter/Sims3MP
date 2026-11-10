// Package server implements the Sims3MP dedicated server. It never touches
// game data formats: the world save and mods are opaque files, simulation
// messages are opaque payloads. What the server owns is identity, admin
// rights, the shared clock, lot/household ownership, ordering of operations
// and the realtime relays (sim state and voice).
package server

import (
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/fsminecrafter/sims3mp/internal/proto"
	"github.com/fsminecrafter/sims3mp/internal/xfer"
)

type Config struct {
	Addr          string  // TCP and UDP listen address, e.g. ":38920"
	DataDir       string  // world, mods and state.json live here
	AdminPassword string  // required to become admin; generated if empty
	JoinPassword  string  // optional password for normal players
	VoiceRange    float64 // voice is not relayed beyond this distance (world units)
	MaxPlayers    int
	MaxOpLog      int
	Logf          func(format string, args ...any)
}

type lot struct {
	Owner    string   `json:"owner"`
	Builders []string `json:"builders,omitempty"`
}

type persisted struct {
	Identities map[string]string `json:"identities"` // name -> sha256(secret)
	Lots       map[uint64]*lot   `json:"lots"`
	Entities   map[uint64]string `json:"entities"` // household/sim -> owner name
	Shared     map[string][]byte `json:"shared"`
	SimMillis  uint64            `json:"simMillis"`
	Epoch      uint32            `json:"epoch"`
	HasWorld   bool              `json:"hasWorld"`
}

type client struct {
	id       uint32
	name     string
	admin    bool
	conn     net.Conn
	out      chan []byte
	done     chan struct{}
	once     sync.Once
	udpKey   uint64
	ready    bool
	sleeping bool
	udpAddr  *net.UDPAddr
	hasPos   bool
	pos      [3]float32
	recv     xfer.Receiver
	upload   int // kind being uploaded, or -1
}

type opRec struct{ frame []byte }

type Server struct {
	cfg Config
	ln  net.Listener
	udp *net.UDPConn

	mu      sync.RWMutex
	st      persisted
	dirty   bool
	state   uint8
	clients map[uint32]*client
	byKey   map[uint64]*client
	nextID  uint32
	seq     uint64
	oplog   [][]byte
	files   [2][]proto.FileEntry
	mode    uint8 // proto.SpeedAuto, or a fixed speed 0..3
	speed   uint8
	lastAdv time.Time

	closed chan struct{}
	wg     sync.WaitGroup
}

func New(cfg Config) (*Server, error) {
	if cfg.Logf == nil {
		cfg.Logf = log.Printf
	}
	if cfg.VoiceRange <= 0 {
		cfg.VoiceRange = 40
	}
	if cfg.MaxPlayers <= 0 {
		cfg.MaxPlayers = 16
	}
	if cfg.MaxOpLog <= 0 {
		cfg.MaxOpLog = 200000
	}
	if cfg.AdminPassword == "" {
		var b [6]byte
		rand.Read(b[:])
		cfg.AdminPassword = hex.EncodeToString(b[:])
		cfg.Logf("generated admin password: %s", cfg.AdminPassword)
	}
	s := &Server{cfg: cfg, clients: map[uint32]*client{}, byKey: map[uint64]*client{},
		mode: proto.SpeedAuto, closed: make(chan struct{}), lastAdv: time.Now()}
	s.st = persisted{Identities: map[string]string{}, Lots: map[uint64]*lot{},
		Entities: map[uint64]string{}, Shared: map[string][]byte{}}
	if err := os.MkdirAll(cfg.DataDir, 0o755); err != nil {
		return nil, err
	}
	if b, err := os.ReadFile(filepath.Join(cfg.DataDir, "state.json")); err == nil {
		if err := json.Unmarshal(b, &s.st); err != nil {
			return nil, fmt.Errorf("state.json: %w", err)
		}
	}
	if s.st.HasWorld {
		if err := s.rescan(); err != nil {
			return nil, err
		}
		s.state = proto.StateRunning
	}
	ln, err := net.Listen("tcp", cfg.Addr)
	if err != nil {
		return nil, err
	}
	ua, _ := net.ResolveUDPAddr("udp", ln.Addr().String())
	udp, err := net.ListenUDP("udp", ua)
	if err != nil {
		ln.Close()
		return nil, err
	}
	s.ln, s.udp = ln, udp
	return s, nil
}

func (s *Server) Addr() string          { return s.ln.Addr().String() }
func (s *Server) AdminPassword() string { return s.cfg.AdminPassword }
func (s *Server) dir(kind uint8) string {
	return filepath.Join(s.cfg.DataDir, [2]string{"world", "mods"}[kind])
}

func (s *Server) rescan() error {
	for k := uint8(0); k < 2; k++ {
		l, err := xfer.Scan(s.dir(k))
		if err != nil {
			return err
		}
		s.files[k] = l
	}
	return nil
}

// Serve runs until Close is called.
func (s *Server) Serve() error {
	s.wg.Add(2)
	go s.udpLoop()
	go s.tickLoop()
	s.cfg.Logf("sims3mp server listening on %s (tcp+udp)", s.Addr())
	for {
		c, err := s.ln.Accept()
		if err != nil {
			select {
			case <-s.closed:
				return nil
			default:
				return err
			}
		}
		if tc, ok := c.(*net.TCPConn); ok {
			tc.SetNoDelay(true)
		}
		s.wg.Add(1)
		go s.handle(c)
	}
}

func (s *Server) Close() {
	select {
	case <-s.closed:
		return
	default:
	}
	close(s.closed)
	s.ln.Close()
	s.udp.Close()
	s.mu.Lock()
	for _, c := range s.clients {
		c.close()
	}
	s.advance(time.Now())
	s.save()
	s.mu.Unlock()
	s.wg.Wait()
}

func (c *client) close() {
	c.once.Do(func() { close(c.done); c.conn.Close() })
}

// send queues a frame without blocking; a client that cannot keep up is dropped
// rather than allowed to stall everyone else.
func (c *client) send(frame []byte) {
	select {
	case c.out <- frame:
	case <-c.done:
	default:
		c.close()
	}
}

// sendWait blocks (used for bulk file data, which must apply backpressure).
func (c *client) sendWait(frame []byte) error {
	select {
	case c.out <- frame:
		return nil
	case <-c.done:
		return errors.New("client gone")
	}
}

func (c *client) msg(typ uint8, w *proto.W) { c.send(proto.Frame(typ, w.B)) }

func (c *client) writer() {
	for {
		select {
		case b := <-c.out:
			c.conn.SetWriteDeadline(time.Now().Add(30 * time.Second))
			if _, err := c.conn.Write(b); err != nil {
				c.close()
				return
			}
		case <-c.done:
			return
		}
	}
}

func hashSecret(s string) string {
	h := sha256.Sum256([]byte(s))
	return hex.EncodeToString(h[:])
}

func validName(n string) bool {
	if len(n) < 1 || len(n) > 24 || strings.TrimSpace(n) != n {
		return false
	}
	for _, r := range n {
		if r < 0x20 || r == 0x7f {
			return false
		}
	}
	return true
}

func eq(a, b string) bool { return subtle.ConstantTimeCompare([]byte(a), []byte(b)) == 1 }

func (s *Server) handle(conn net.Conn) {
	defer s.wg.Done()
	c := &client{conn: conn, out: make(chan []byte, 2048), done: make(chan struct{}), upload: -1}
	go c.writer()
	defer c.close()

	conn.SetReadDeadline(time.Now().Add(10 * time.Second))
	typ, p, err := proto.ReadFrame(conn)
	if err != nil || typ != proto.CHello {
		return
	}
	r := proto.R{B: p}
	ver, name, secret, pass := r.U16(), r.Str(), r.Str(), r.Str()
	reject := func(why string) {
		c.msg(proto.SReject, (&proto.W{}).Str(why))
		time.Sleep(100 * time.Millisecond) // let the writer flush
	}
	switch {
	case r.Err || ver != proto.Version:
		reject(fmt.Sprintf("protocol version mismatch (server %d)", proto.Version))
		return
	case !validName(name) || len(secret) < 8:
		reject("invalid player name or identity")
		return
	}
	c.admin = eq(pass, s.cfg.AdminPassword)
	if !c.admin && s.cfg.JoinPassword != "" && !eq(pass, s.cfg.JoinPassword) {
		reject("wrong password")
		return
	}
	var kb [8]byte
	rand.Read(kb[:])
	c.udpKey = binary.LittleEndian.Uint64(kb[:])
	c.name = name

	s.mu.Lock()
	if known, ok := s.st.Identities[name]; ok && known != hashSecret(secret) {
		s.mu.Unlock()
		reject("that player name belongs to someone else on this server")
		return
	}
	for _, o := range s.clients {
		if o.name == name {
			s.mu.Unlock()
			reject("that player is already connected")
			return
		}
	}
	if len(s.clients) >= s.cfg.MaxPlayers {
		s.mu.Unlock()
		reject("server is full")
		return
	}
	if _, ok := s.st.Identities[name]; !ok {
		s.st.Identities[name] = hashSecret(secret)
		s.dirty = true
	}
	s.nextID++
	c.id = s.nextID
	c.msg(proto.SWelcome, (&proto.W{}).U32(c.id).Bool(c.admin).U64(c.udpKey).U8(s.state).U32(s.st.Epoch))
	join := proto.Frame(proto.SPlayerJoin, (&proto.W{}).U32(c.id).Str(c.name).Bool(c.admin).B)
	for _, o := range s.clients {
		o.send(join)
		c.msg(proto.SPlayerJoin, (&proto.W{}).U32(o.id).Str(o.name).Bool(o.admin))
	}
	c.send(join)
	s.clients[c.id] = c
	s.byKey[c.udpKey] = c
	if s.state == proto.StateRunning {
		s.sendFileLists(c)
	}
	s.mu.Unlock()
	s.cfg.Logf("%s joined (id %d, admin=%v) from %s", c.name, c.id, c.admin, conn.RemoteAddr())

	defer func() {
		c.recv.Abort()
		s.mu.Lock()
		delete(s.clients, c.id)
		delete(s.byKey, c.udpKey)
		if c.upload == proto.KindWorld && s.state == proto.StateInitializing {
			s.setState(map[bool]uint8{true: proto.StateRunning, false: proto.StateWaitingForWorld}[s.st.HasWorld])
		}
		s.broadcast(proto.Frame(proto.SPlayerLeave, (&proto.W{}).U32(c.id).B), nil)
		s.recalcSpeed()
		s.mu.Unlock()
		s.cfg.Logf("%s left", c.name)
	}()

	for {
		conn.SetReadDeadline(time.Now().Add(60 * time.Second))
		typ, p, err := proto.ReadFrame(conn)
		if err != nil {
			return
		}
		if err := s.dispatch(c, typ, p); err != nil {
			s.cfg.Logf("dropping %s: %v", c.name, err)
			c.msg(proto.SNotice, (&proto.W{}).Str("disconnected: "+err.Error()))
			time.Sleep(50 * time.Millisecond)
			return
		}
	}
}

func (s *Server) sendFileLists(c *client) {
	for k := uint8(0); k < 2; k++ {
		c.send(s.fileListFrame(k))
	}
}

func (s *Server) fileListFrame(k uint8) []byte {
	w := (&proto.W{}).U8(k).U32(s.st.Epoch).U32(uint32(len(s.files[k])))
	for _, e := range s.files[k] {
		w.Str(e.Path).U64(e.Size).Raw(e.Sum[:])
	}
	return proto.Frame(proto.SFileList, w.B)
}

func (s *Server) broadcast(frame []byte, readyOnly func(*client) bool) {
	for _, o := range s.clients {
		if readyOnly == nil || readyOnly(o) {
			o.send(frame)
		}
	}
}

func isReady(c *client) bool { return c.ready }

func (s *Server) setState(st uint8) {
	s.advance(time.Now())
	s.state = st
	s.broadcast(proto.Frame(proto.SState, (&proto.W{}).U8(st).U32(s.st.Epoch).B), nil)
	s.recalcSpeed()
}

var errBad = errors.New("malformed message")
var errAdmin = errors.New("admin only")

func (s *Server) dispatch(c *client, typ uint8, p []byte) error {
	r := proto.R{B: p}
	// File payloads are handled outside the state lock: disk I/O must never
	// delay the realtime paths.
	switch typ {
	case proto.CFileBegin:
		if c.upload < 0 {
			return errAdmin
		}
		_, err := c.recv.Begin(p, func(kind uint8) (string, bool) {
			return s.dir(kind) + ".incoming", int(kind) == c.upload
		})
		return err
	case proto.CFileChunk:
		return c.recv.Chunk(p)
	case proto.CFileEnd:
		_, err := c.recv.End()
		return err
	case proto.CRequestFile:
		kind, path := r.U8(), r.Str()
		if r.Err || kind > 1 {
			return errBad
		}
		s.mu.RLock()
		var ent *proto.FileEntry
		for i := range s.files[kind] {
			if s.files[kind][i].Path == path {
				e := s.files[kind][i]
				ent = &e
			}
		}
		running := s.state == proto.StateRunning
		s.mu.RUnlock()
		if ent == nil || !running {
			c.msg(proto.SNotice, (&proto.W{}).Str("file not available: "+path))
			return nil
		}
		return xfer.Send(s.dir(kind), kind, *ent, c.sendWait)
	case proto.CPing:
		c.msg(proto.SPong, (&proto.W{}).U64(r.U64()))
		return nil
	}

	s.mu.Lock()
	defer s.mu.Unlock()
	switch typ {
	case proto.CUploadBegin:
		kind := r.U8()
		if !c.admin {
			return errAdmin
		}
		if r.Err || kind > 1 || c.upload >= 0 {
			return errBad
		}
		for _, o := range s.clients {
			if o != c && o.upload == int(kind) {
				return errors.New("another admin is already uploading")
			}
		}
		if err := os.RemoveAll(s.dir(kind) + ".incoming"); err != nil {
			return err
		}
		if err := os.MkdirAll(s.dir(kind)+".incoming", 0o755); err != nil {
			return err
		}
		c.upload = int(kind)
		if kind == proto.KindWorld {
			s.setState(proto.StateInitializing)
		}
	case proto.CUploadEnd:
		kind := r.U8()
		if r.Err || int(kind) != c.upload || c.recv.Active() {
			return errBad
		}
		c.upload = -1
		os.RemoveAll(s.dir(kind) + ".old")
		os.Rename(s.dir(kind), s.dir(kind)+".old")
		if err := os.Rename(s.dir(kind)+".incoming", s.dir(kind)); err != nil {
			return err
		}
		os.RemoveAll(s.dir(kind) + ".old")
		if err := s.rescan(); err != nil {
			return err
		}
		if kind == proto.KindWorld {
			// A new world is a new epoch: everything recorded against the old
			// save is already baked into the new one.
			s.st.Epoch++
			s.st.HasWorld = true
			s.oplog = nil
			for _, o := range s.clients {
				o.ready = false
			}
			s.dirty = true
			s.setState(proto.StateRunning)
			for _, o := range s.clients {
				s.sendFileLists(o)
			}
			s.cfg.Logf("world epoch %d uploaded by %s (%d files)", s.st.Epoch, c.name, len(s.files[0]))
		} else if s.state == proto.StateRunning {
			s.broadcast(s.fileListFrame(proto.KindMods), nil)
		}
	case proto.CReady:
		if s.state != proto.StateRunning {
			return nil
		}
		c.ready = true
		c.sleeping = false
		s.snapshot(c)
		s.recalcSpeed()
	case proto.COp:
		cat, lotID, ent := r.U8(), r.U64(), r.U64()
		payload := r.Bytes()
		if r.Err {
			return errBad
		}
		if !c.ready {
			return nil
		}
		if why := s.checkOp(c, cat, lotID, ent); why != "" {
			c.msg(proto.SOpRejected, (&proto.W{}).U8(cat).U64(lotID).U64(ent).Str(why))
			return nil
		}
		s.seq++
		f := proto.Frame(proto.SOp, (&proto.W{}).U64(s.seq).U32(c.id).U8(cat).U64(lotID).U64(ent).Bytes(payload).B)
		if len(s.oplog) < s.cfg.MaxOpLog {
			s.oplog = append(s.oplog, f)
			if len(s.oplog) == s.cfg.MaxOpLog {
				s.notifyAdmins("Operation log is full: late joiners will be out of date until an admin re-uploads the world.")
			}
		}
		s.broadcast(f, isReady)
	case proto.CSetSleeping:
		c.sleeping = r.Bool()
		s.recalcSpeed()
	case proto.CSetSpeed:
		m := r.U8()
		if !c.admin {
			return s.deny(c, "Only the admin can change the game speed.")
		}
		if m > 3 && m != proto.SpeedAuto {
			return errBad
		}
		s.mode = m
		s.recalcSpeed()
	case proto.CClaimLot:
		id := r.U64()
		if r.Err || id == 0 {
			return errBad
		}
		l := s.st.Lots[id]
		if l != nil && l.Owner != "" && l.Owner != c.name {
			return s.deny(c, "That lot is already owned by "+l.Owner+".")
		}
		if l == nil {
			l = &lot{}
			s.st.Lots[id] = l
		}
		l.Owner = c.name
		s.lotChanged(id)
	case proto.CReleaseLot:
		id := r.U64()
		if l := s.st.Lots[id]; l != nil && (l.Owner == c.name || c.admin) {
			delete(s.st.Lots, id)
			s.lotChanged(id)
		}
	case proto.CSetLotOwner:
		id, who := r.U64(), r.Str()
		if !c.admin {
			return s.deny(c, "Only the admin can reassign lots.")
		}
		if r.Err || id == 0 {
			return errBad
		}
		if who == "" {
			delete(s.st.Lots, id)
		} else {
			s.st.Lots[id] = &lot{Owner: who}
		}
		s.lotChanged(id)
	case proto.CSetBuilder:
		id, who, add := r.U64(), r.Str(), r.Bool()
		l := s.st.Lots[id]
		if r.Err || !validName(who) {
			return errBad
		}
		if l == nil || (l.Owner != c.name && !c.admin) {
			return s.deny(c, "You can only manage builders on a lot you own.")
		}
		l.Builders = remove(l.Builders, who)
		if add && who != l.Owner && len(l.Builders) < 64 {
			l.Builders = append(l.Builders, who)
			sort.Strings(l.Builders)
		}
		s.lotChanged(id)
	case proto.CClaimEntity:
		id := r.U64()
		if r.Err || id == 0 {
			return errBad
		}
		if o, ok := s.st.Entities[id]; ok && o != c.name {
			return s.deny(c, "That household is already played by "+o+".")
		}
		s.st.Entities[id] = c.name
		s.entityChanged(id)
	case proto.CReleaseEnt:
		id := r.U64()
		if o, ok := s.st.Entities[id]; ok && (o == c.name || c.admin) {
			delete(s.st.Entities, id)
			s.entityChanged(id)
		}
	case proto.CSetShared:
		key := r.Str()
		val := r.Bytes()
		if r.Err || key == "" || len(key) > 128 || len(val) > 64<<10 {
			return errBad
		}
		if strings.HasPrefix(key, "admin.") && !c.admin {
			return s.deny(c, "That setting can only be changed by the admin.")
		}
		if _, exists := s.st.Shared[key]; !exists && len(s.st.Shared) >= 4096 {
			return s.deny(c, "Shared data store is full.")
		}
		s.st.Shared[key] = val
		s.dirty = true
		s.broadcast(proto.Frame(proto.SShared, (&proto.W{}).Str(key).Bytes(val).B), nil)
	case proto.CChat:
		t := r.Str()
		if r.Err || len(t) > 500 {
			return errBad
		}
		s.broadcast(proto.Frame(proto.SChat, (&proto.W{}).U32(c.id).Str(t).B), nil)
	case proto.CKick:
		who := r.Str()
		if !c.admin {
			return errAdmin
		}
		for _, o := range s.clients {
			if o.name == who && o != c {
				o.msg(proto.SNotice, (&proto.W{}).Str("You were kicked by the admin."))
				go func(o *client) { time.Sleep(100 * time.Millisecond); o.close() }(o)
			}
		}
	default:
		return fmt.Errorf("unknown message %d", typ)
	}
	if r.Err {
		return errBad
	}
	return nil
}

func remove(l []string, s string) []string {
	out := l[:0]
	for _, v := range l {
		if v != s {
			out = append(out, v)
		}
	}
	return out
}

func (s *Server) deny(c *client, why string) error {
	c.msg(proto.SNotice, (&proto.W{}).Str(why))
	return nil
}

func (s *Server) notifyAdmins(text string) {
	s.broadcast(proto.Frame(proto.SNotice, (&proto.W{}).Str(text).B), func(o *client) bool { return o.admin })
}

// canBuild is the lot permission rule: owner, listed builder, or admin.
// Unowned lots are admin-only until somebody buys (claims) them.
func (s *Server) canBuild(c *client, lotID uint64) bool {
	if c.admin {
		return true
	}
	l := s.st.Lots[lotID]
	if l == nil {
		return false
	}
	if l.Owner == c.name {
		return true
	}
	for _, b := range l.Builders {
		if b == c.name {
			return true
		}
	}
	return false
}

func (s *Server) checkOp(c *client, cat uint8, lotID, ent uint64) string {
	switch cat {
	case proto.OpGeneral:
		if ent != 0 && s.st.Entities[ent] != c.name && !c.admin {
			return "you do not control that household"
		}
	case proto.OpBuild:
		if !s.canBuild(c, lotID) {
			return "you are not allowed to build on that lot"
		}
	case proto.OpAdmin:
		if !c.admin {
			return "admin only"
		}
	case proto.OpWorld:
	default:
		return "unknown operation category"
	}
	return ""
}

func (s *Server) lotFrame(id uint64) []byte {
	w := (&proto.W{}).U64(id)
	if l := s.st.Lots[id]; l != nil {
		w.Str(l.Owner).U16(uint16(len(l.Builders)))
		for _, b := range l.Builders {
			w.Str(b)
		}
	} else {
		w.Str("").U16(0)
	}
	return proto.Frame(proto.SLot, w.B)
}

func (s *Server) lotChanged(id uint64) {
	s.dirty = true
	s.broadcast(s.lotFrame(id), nil)
}

func (s *Server) entityChanged(id uint64) {
	s.dirty = true
	s.broadcast(proto.Frame(proto.SEntity, (&proto.W{}).U64(id).Str(s.st.Entities[id]).B), nil)
}

// snapshot brings a client that just loaded the world up to date.
func (s *Server) snapshot(c *client) {
	for _, o := range s.clients {
		c.msg(proto.SPlayerJoin, (&proto.W{}).U32(o.id).Str(o.name).Bool(o.admin))
	}
	for id := range s.st.Lots {
		c.send(s.lotFrame(id))
	}
	for id, o := range s.st.Entities {
		c.msg(proto.SEntity, (&proto.W{}).U64(id).Str(o))
	}
	for k, v := range s.st.Shared {
		c.msg(proto.SShared, (&proto.W{}).Str(k).Bytes(v))
	}
	for _, f := range s.oplog {
		c.send(f)
	}
	c.send(s.clockFrame())
	c.send(proto.Frame(proto.SSnapshotEnd, nil))
}

// --- clock ---------------------------------------------------------------

func (s *Server) advance(now time.Time) {
	if s.state == proto.StateRunning {
		s.st.SimMillis += uint64(now.Sub(s.lastAdv).Milliseconds()) * uint64(s.speed)
	}
	s.lastAdv = now
}

func (s *Server) clockFrame() []byte {
	return proto.Frame(proto.SClock, (&proto.W{}).U64(s.st.SimMillis).U8(s.speed).U8(s.mode).B)
}

// recalcSpeed applies the time rule: 1x normally, 3x while every player in
// the world is asleep, unless an admin pinned a speed. With nobody in the
// world, or while the world is being (re)initialised, time stands still.
func (s *Server) recalcSpeed() {
	n, asleep := 0, 0
	for _, c := range s.clients {
		if c.ready {
			n++
			if c.sleeping {
				asleep++
			}
		}
	}
	var sp uint8
	switch {
	case s.state != proto.StateRunning || n == 0:
		sp = 0
	case s.mode != proto.SpeedAuto:
		sp = s.mode
	case asleep == n:
		sp = 3
	default:
		sp = 1
	}
	if sp != s.speed {
		s.advance(time.Now())
		s.speed = sp
		s.dirty = true
		s.broadcast(s.clockFrame(), isReady)
	}
}

func (s *Server) tickLoop() {
	defer s.wg.Done()
	t := time.NewTicker(time.Second)
	defer t.Stop()
	n := 0
	for {
		select {
		case <-s.closed:
			return
		case now := <-t.C:
			s.mu.Lock()
			s.advance(now)
			s.broadcast(s.clockFrame(), isReady)
			if n++; n%5 == 0 && (s.dirty || s.speed != 0) {
				s.save()
			}
			s.mu.Unlock()
		}
	}
}

func (s *Server) save() {
	b, err := json.MarshalIndent(&s.st, "", " ")
	if err == nil {
		tmp := filepath.Join(s.cfg.DataDir, "state.json.tmp")
		if err = os.WriteFile(tmp, b, 0o600); err == nil {
			err = os.Rename(tmp, filepath.Join(s.cfg.DataDir, "state.json"))
		}
	}
	if err != nil {
		s.cfg.Logf("saving state: %v", err)
		return
	}
	s.dirty = false
}

// --- UDP relay -----------------------------------------------------------

func (s *Server) udpLoop() {
	defer s.wg.Done()
	buf := make([]byte, 2048)
	out := make([]byte, 0, 2048)
	var targets []*net.UDPAddr
	rng2 := float32(s.cfg.VoiceRange * s.cfg.VoiceRange)
	for {
		n, addr, err := s.udp.ReadFromUDP(buf)
		if err != nil {
			select {
			case <-s.closed:
				return
			default:
				continue
			}
		}
		if n < 9 {
			continue
		}
		typ, key := buf[0], binary.LittleEndian.Uint64(buf[1:9])
		r := proto.R{B: buf[9:n]}
		targets, out = targets[:0], out[:0]

		s.mu.Lock()
		c := s.byKey[key]
		if c == nil {
			s.mu.Unlock()
			continue
		}
		first := c.udpAddr == nil
		if first || !c.udpAddr.IP.Equal(addr.IP) || c.udpAddr.Port != addr.Port {
			c.udpAddr = addr
		}
		switch typ {
		case proto.UPosition:
			x, y, z := r.F32(), r.F32(), r.F32()
			if !r.Err {
				c.pos, c.hasPos = [3]float32{x, y, z}, true
			}
			if first {
				out = append(out, proto.UAck)
				targets = append(targets, addr)
			}
		case proto.UState:
			ent := r.U64()
			if r.Err || !c.ready || (s.st.Entities[ent] != c.name && !c.admin) {
				break
			}
			out = append(out, proto.UStateRelay)
			out = binary.LittleEndian.AppendUint32(out, c.id)
			out = append(out, buf[9:n]...)
			for _, o := range s.clients {
				if o != c && o.ready && o.udpAddr != nil {
					targets = append(targets, o.udpAddr)
				}
			}
		case proto.UVoice:
			if n < 9+14 || !c.ready {
				break
			}
			r.U16()
			p := [3]float32{r.F32(), r.F32(), r.F32()}
			c.pos, c.hasPos = p, true
			out = append(out, proto.UVoiceRelay)
			out = binary.LittleEndian.AppendUint32(out, c.id)
			out = append(out, buf[9:n]...)
			for _, o := range s.clients {
				if o == c || !o.ready || o.udpAddr == nil {
					continue
				}
				if o.hasPos {
					dx, dy, dz := o.pos[0]-p[0], o.pos[1]-p[1], o.pos[2]-p[2]
					if dx*dx+dy*dy+dz*dz > rng2 {
						continue
					}
				}
				targets = append(targets, o.udpAddr)
			}
		}
		s.mu.Unlock()
		for _, t := range targets {
			s.udp.WriteToUDP(out, t)
		}
	}
}
