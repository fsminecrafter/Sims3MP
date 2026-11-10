// Command sims3mp-bridge runs next to The Sims 3 on each player's PC. It is
// started automatically by the native shim (Sims3MPNative.dll).
package main

import (
	"flag"
	"log"
	"os"
	"path/filepath"
	"runtime"

	"github.com/fsminecrafter/sims3mp/internal/bridge"
	"github.com/fsminecrafter/sims3mp/internal/voice"
)

func main() {
	exe, _ := os.Executable()
	serverExe := filepath.Join(filepath.Dir(exe), "sims3mp-server")
	if runtime.GOOS == "windows" {
		serverExe += ".exe"
	}
	home, _ := os.UserHomeDir()
	cfg := bridge.Config{Audio: voice.OpenDevice}
	flag.StringVar(&cfg.LocalAddr, "local", "127.0.0.1:38921", "loopback address the game connects to")
	flag.StringVar(&cfg.TS3Dir, "ts3dir", filepath.Join(home, "Documents", "Electronic Arts", "The Sims 3"), "The Sims 3 user data folder")
	flag.StringVar(&cfg.ServerExe, "server-exe", serverExe, "dedicated server binary used for hosting on this PC")
	novoice := flag.Bool("no-voice", false, "disable voice chat")
	flag.Parse()
	if *novoice {
		cfg.Audio = nil
	}
	if err := os.MkdirAll(filepath.Join(cfg.TS3Dir, "Sims3MP"), 0o755); err == nil {
		if f, err := os.OpenFile(filepath.Join(cfg.TS3Dir, "Sims3MP", "bridge.log"), os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600); err == nil {
			log.SetOutput(f)
		}
	}
	b, err := bridge.New(cfg)
	if err != nil {
		log.Fatal(err) // usually: another bridge is already running, which is fine
	}
	log.Printf("bridge listening on %s, game data in %s", b.LocalAddr(), cfg.TS3Dir)
	log.Fatal(b.Run())
}
