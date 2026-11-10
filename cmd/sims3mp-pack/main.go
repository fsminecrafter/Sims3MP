// Command sims3mp-pack turns the compiled script mod into Sims3MP.package.
package main

import (
	"flag"
	"log"
	"os"

	"github.com/fsminecrafter/sims3mp/internal/dbpf"
)

func main() {
	dll := flag.String("dll", "", "compiled mod assembly (Sims3MP.dll)")
	xml := flag.String("xml", "mod/Sims3MP.Main.xml", "tuning XML that makes the game start the mod")
	class := flag.String("class", "Sims3MP.Main", "full name of the class the tuning XML belongs to")
	out := flag.String("out", "Sims3MP.package", "package to write")
	flag.Parse()
	asm, err := os.ReadFile(*dll)
	if err != nil {
		log.Fatal(err)
	}
	tuning, err := os.ReadFile(*xml)
	if err != nil {
		log.Fatal(err)
	}
	pkg := dbpf.Write([]dbpf.Resource{
		{Type: dbpf.TypeS3SA, Instance: dbpf.FNV64("Sims3MP"), Data: dbpf.ScriptResource(asm)},
		{Type: dbpf.TypeXML, Instance: dbpf.FNV64(*class), Data: tuning},
	})
	if _, err := dbpf.Read(pkg); err != nil {
		log.Fatal(err)
	}
	if err := os.WriteFile(*out, pkg, 0o644); err != nil {
		log.Fatal(err)
	}
	log.Printf("wrote %s (%d bytes)", *out, len(pkg))
}
