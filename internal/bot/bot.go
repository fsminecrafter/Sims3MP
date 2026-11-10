// Package bot is a scriptable stand-in for a second player, for testing a
// server (and the game talking to it) without a second copy of the game.
// It speaks the real protocol: it shows up in the player list, can chat, go
// to sleep, claim lots and make the host a builder, and prints everything
// the server tells it.
package bot

import (
	"bufio"
	"crypto/rand"
	"encoding/hex"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/fsminecrafter/sims3mp/internal/proto"
)

type Config struct {
	Server   string
	Name     string
	Password string
	// IdentityFile keeps this bot's identity key between runs. Empty: a
	// file in the user config directory named after the bot.
	IdentityFile string
}

type Bot struct {
	cfg Config
	out io.Writer

	mu      sync.Mutex
	conn    net.Conn
	players map[uint32]string
	me      uint32
	admin   bool
	ready   bool
	state   uint8
	speed   uint8
	lots    map[uint64]string
}

func New(cfg Config, out io.Writer) *Bot {
	if !strings.Contains(cfg.Server, ":") {
		cfg.Server += ":38920"
	}
	return &Bot{cfg: cfg, out: out, players: map[uint32]string{}, lots: map[uint64]string{}}
}

func (b *Bot) say(format string, args ...any) {
	b.mu.Lock()
	defer b.mu.Unlock()
	fmt.Fprintf(b.out, time.Now().Format("15:04:05 ")+format+"\n", args...)
}

func (b *Bot) identity() (string, error) {
	p := b.cfg.IdentityFile
	if p == "" {
		dir, err := os.UserConfigDir()
		if err != nil {
			dir = os.TempDir()
		}
		safe := strings.Map(func(r rune) rune {
			if r == '/' || r == '\\' || r == ':' {
				return '_'
			}
			return r
		}, b.cfg.Name)
		p = filepath.Join(dir, "sims3mp", "bot-"+safe+".key")
	}
	if d, err := os.ReadFile(p); err == nil && len(strings.TrimSpace(string(d))) >= 16 {
		return strings.TrimSpace(string(d)), nil
	}
	var k [16]byte
	rand.Read(k[:])
	s := hex.EncodeToString(k[:])
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return "", err
	}
	return s, os.WriteFile(p, []byte(s), 0o600)
}

func (b *Bot) send(typ uint8, w *proto.W) error {
	b.mu.Lock()
	c := b.conn
	b.mu.Unlock()
	if c == nil {
		return fmt.Errorf("not connected")
	}
	var payload []byte
	if w != nil {
		payload = w.B
	}
	c.SetWriteDeadline(time.Now().Add(10 * time.Second))
	_, err := c.Write(proto.Frame(typ, payload))
	return err
}

// Connect dials until it succeeds or stop is closed; the server may not be
// running yet when the bot starts.
func (b *Bot) Connect(stop <-chan struct{}) error {
	secret, err := b.identity()
	if err != nil {
		return err
	}
	announced := false
	for {
		c, err := net.DialTimeout("tcp", b.cfg.Server, 5*time.Second)
		if err == nil {
			c.(*net.TCPConn).SetNoDelay(true)
			c.Write(proto.Frame(proto.CHello, (&proto.W{}).U16(proto.Version).Str(b.cfg.Name).Str(secret).Str(b.cfg.Password).B))
			c.SetReadDeadline(time.Now().Add(10 * time.Second))
			typ, p, rerr := proto.ReadFrame(c)
			r := proto.R{B: p}
			switch {
			case rerr != nil:
				c.Close()
			case typ == proto.SReject:
				c.Close()
				return fmt.Errorf("the server refused the bot: %s", r.Str())
			case typ == proto.SWelcome:
				b.me, b.admin = r.U32(), r.Bool()
				r.U64()
				b.state = r.U8()
				b.mu.Lock()
				b.conn = c
				b.mu.Unlock()
				b.say("connected to %s as %q (player %d%s)", b.cfg.Server, b.cfg.Name, b.me, map[bool]string{true: ", admin"}[b.admin])
				go b.keepAlive(c)
				go b.reader(c)
				if b.state == proto.StateRunning {
					b.enter()
				} else {
					b.say("the server has no world yet; waiting for the host to start it")
				}
				return nil
			}
		}
		if !announced {
			announced = true
			b.say("waiting for the server at %s ...", b.cfg.Server)
		}
		select {
		case <-stop:
			return fmt.Errorf("stopped")
		case <-time.After(3 * time.Second):
		}
	}
}

// enter joins the running world. The bot has no game, so it takes no files.
func (b *Bot) enter() {
	b.mu.Lock()
	already := b.ready
	b.ready = true
	b.mu.Unlock()
	if !already {
		b.send(proto.CReady, nil)
	}
}

func (b *Bot) keepAlive(c net.Conn) {
	for {
		time.Sleep(3 * time.Second)
		b.mu.Lock()
		cur := b.conn
		b.mu.Unlock()
		if cur != c || b.send(proto.CPing, (&proto.W{}).U64(uint64(time.Now().UnixMicro()))) != nil {
			return
		}
	}
}

func (b *Bot) name(id uint32) string {
	b.mu.Lock()
	defer b.mu.Unlock()
	if n, ok := b.players[id]; ok {
		return n
	}
	return fmt.Sprintf("player %d", id)
}

func (b *Bot) reader(c net.Conn) {
	for {
		c.SetReadDeadline(time.Now().Add(60 * time.Second))
		typ, p, err := proto.ReadFrame(c)
		if err != nil {
			b.mu.Lock()
			if b.conn == c {
				b.conn = nil
			}
			b.mu.Unlock()
			b.say("disconnected from the server")
			return
		}
		r := proto.R{B: p}
		switch typ {
		case proto.SPlayerJoin:
			id, name, admin := r.U32(), r.Str(), r.Bool()
			b.mu.Lock()
			_, known := b.players[id]
			b.players[id] = name
			b.mu.Unlock()
			if !known && id != b.me {
				b.say("%s joined%s", name, map[bool]string{true: " (admin)"}[admin])
			}
		case proto.SPlayerLeave:
			id := r.U32()
			b.say("%s left", b.name(id))
			b.mu.Lock()
			delete(b.players, id)
			b.mu.Unlock()
		case proto.SState:
			st := r.U8()
			b.mu.Lock()
			b.state = st
			if st != proto.StateRunning {
				b.ready = false
			}
			b.mu.Unlock()
			switch st {
			case proto.StateInitializing:
				b.say("the host is uploading a new world; everything pauses")
			case proto.StateRunning:
				b.say("the world is running")
				b.enter()
			}
		case proto.SClock:
			r.U64()
			sp := r.U8()
			b.mu.Lock()
			changed := sp != b.speed
			b.speed = sp
			b.mu.Unlock()
			if changed {
				b.say("game speed is now %dx", sp)
			}
		case proto.SLot:
			id, owner := r.U64(), r.Str()
			n := int(r.U16())
			var builders []string
			for i := 0; i < n && !r.Err; i++ {
				builders = append(builders, r.Str())
			}
			b.mu.Lock()
			b.lots[id] = owner
			b.mu.Unlock()
			if owner == "" {
				b.say("lot %d is no longer owned", id)
			} else {
				b.say("lot %d: owner %s, builders %v", id, owner, builders)
			}
		case proto.SEntity:
			id, owner := r.U64(), r.Str()
			if owner != "" {
				b.say("household %d is played by %s", id, owner)
			}
		case proto.SChat:
			from := r.U32()
			b.say("%s: %s", b.name(from), r.Str())
		case proto.SNotice:
			b.say("server: %s", r.Str())
		case proto.SOpRejected:
			r.U8()
			r.U64()
			r.U64()
			b.say("the server rejected an action: %s", r.Str())
		case proto.SSnapshotEnd:
			b.say("caught up with the world")
		}
	}
}

const help = `commands:
  chat <text>                    say something to everyone
  sleep | wake                   mark all of the bot's sims asleep / awake
  claim-lot <id>                 buy a lot (the host can then no longer build there)
  release-lot <id>
  builder <lot> <player> add|remove    let a player build on the bot's lot
  household <id>                 play a household
  speed <0|1|2|3|auto>           game speed (only works if the bot is admin)
  players | lots                 list them
  quit`

// Repl runs commands from in until it ends or "quit".
func (b *Bot) Repl(in io.Reader) {
	fmt.Fprintln(b.out, help)
	sc := bufio.NewScanner(in)
	for sc.Scan() {
		if !b.Command(sc.Text()) {
			return
		}
	}
}

// Command runs one command line; false means quit.
func (b *Bot) Command(line string) bool {
	f := strings.Fields(line)
	if len(f) == 0 {
		return true
	}
	num := func(i int) uint64 {
		if i >= len(f) {
			return 0
		}
		v, _ := strconv.ParseUint(f[i], 10, 64)
		return v
	}
	var err error
	switch strings.ToLower(f[0]) {
	case "help", "?":
		fmt.Fprintln(b.out, help)
	case "quit", "exit":
		return false
	case "chat", "say":
		err = b.send(proto.CChat, (&proto.W{}).Str(strings.TrimSpace(strings.TrimPrefix(line, f[0]))))
	case "sleep":
		err = b.send(proto.CSetSleeping, (&proto.W{}).Bool(true))
	case "wake":
		err = b.send(proto.CSetSleeping, (&proto.W{}).Bool(false))
	case "claim-lot":
		err = b.send(proto.CClaimLot, (&proto.W{}).U64(num(1)))
	case "release-lot":
		err = b.send(proto.CReleaseLot, (&proto.W{}).U64(num(1)))
	case "builder":
		if len(f) < 4 {
			b.say("usage: builder <lot> <player> add|remove")
			break
		}
		err = b.send(proto.CSetBuilder, (&proto.W{}).U64(num(1)).Str(f[2]).Bool(strings.EqualFold(f[3], "add")))
	case "household":
		err = b.send(proto.CClaimEntity, (&proto.W{}).U64(num(1)))
	case "speed":
		if len(f) < 2 {
			b.say("usage: speed <0|1|2|3|auto>")
			break
		}
		m := uint8(proto.SpeedAuto)
		if !strings.EqualFold(f[1], "auto") {
			m = uint8(num(1))
		}
		err = b.send(proto.CSetSpeed, (&proto.W{}).U8(m))
	case "players":
		b.mu.Lock()
		for id, n := range b.players {
			fmt.Fprintf(b.out, "  %d  %s\n", id, n)
		}
		b.mu.Unlock()
	case "lots":
		b.mu.Lock()
		for id, o := range b.lots {
			fmt.Fprintf(b.out, "  lot %d  owner %q\n", id, o)
		}
		b.mu.Unlock()
	default:
		b.say("unknown command %q (type help)", f[0])
	}
	if err != nil {
		b.say("could not send: %v", err)
	}
	return true
}

func (b *Bot) Close() {
	b.mu.Lock()
	c := b.conn
	b.conn = nil
	b.mu.Unlock()
	if c != nil {
		c.Close()
	}
}
