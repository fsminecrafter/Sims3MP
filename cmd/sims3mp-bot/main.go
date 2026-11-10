// Command sims3mp-bot joins a Sims3MP server as a computer-controlled
// second player, so multiplayer can be tested with a single game.
package main

import (
	"flag"
	"fmt"
	"os"

	"github.com/fsminecrafter/sims3mp/internal/bot"
)

func main() {
	var cfg bot.Config
	flag.StringVar(&cfg.Server, "server", "127.0.0.1:38920", "server address")
	flag.StringVar(&cfg.Name, "name", "Bot", "player name")
	flag.StringVar(&cfg.Password, "password", os.Getenv("SIMS3MP_PASSWORD"), "join password, if the server has one")
	flag.Parse()

	b := bot.New(cfg, os.Stdout)
	defer b.Close()
	if err := b.Connect(nil); err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		fmt.Fprintln(os.Stderr, "Press Enter to close.")
		fmt.Scanln()
		os.Exit(1)
	}
	b.Repl(os.Stdin)
}
