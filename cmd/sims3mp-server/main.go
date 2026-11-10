// Command sims3mp-server is the Sims3MP dedicated server. It does not need
// The Sims 3 installed: the world is uploaded by the admin's game client.
package main

import (
	"flag"
	"log"
	"os"
	"os/signal"

	"github.com/fsminecrafter/sims3mp/internal/server"
)

func main() {
	var cfg server.Config
	flag.StringVar(&cfg.Addr, "addr", ":38920", "listen address (TCP and UDP)")
	flag.StringVar(&cfg.DataDir, "data", "sims3mp-data", "directory for the world, mods and server state")
	flag.StringVar(&cfg.AdminPassword, "admin-password", os.Getenv("SIMS3MP_ADMIN_PASSWORD"), "password that grants admin rights (generated and printed if empty)")
	flag.StringVar(&cfg.JoinPassword, "password", os.Getenv("SIMS3MP_PASSWORD"), "optional password required to join")
	flag.Float64Var(&cfg.VoiceRange, "voice-range", 40, "maximum distance at which voice chat is relayed")
	flag.IntVar(&cfg.MaxPlayers, "max-players", 16, "maximum simultaneous players")
	flag.Parse()

	s, err := server.New(cfg)
	if err != nil {
		log.Fatal(err)
	}
	go func() {
		ch := make(chan os.Signal, 1)
		signal.Notify(ch, os.Interrupt)
		<-ch
		log.Print("shutting down")
		s.Close()
	}()
	if err := s.Serve(); err != nil {
		log.Fatal(err)
	}
}
