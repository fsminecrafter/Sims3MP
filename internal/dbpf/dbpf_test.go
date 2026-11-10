package dbpf

import (
	"bytes"
	"testing"
)

func TestFNV64(t *testing.T) {
	// Well-known FNV-1 64 test vectors; names hash case-insensitively.
	if FNV64("") != 0xCBF29CE484222325 || FNV64("a") != 0xAF63BD4C8601B7BE {
		t.Fatal("FNV-1 64 mismatch")
	}
	if FNV64("Sims3MP.Main") != FNV64("sims3mp.main") {
		t.Fatal("hash must ignore case")
	}
}

func TestRoundTrip(t *testing.T) {
	dll := bytes.Repeat([]byte{0x4D, 0x5A, 7}, 700) // 2100 bytes: spans 5 blocks
	s3sa := ScriptResource(dll)
	if want := 1 + 4 + 64 + 2 + 5*8 + 5*512; len(s3sa) != want {
		t.Fatalf("S3SA size %d, want %d", len(s3sa), want)
	}
	if !bytes.Equal(s3sa[len(s3sa)-5*512:][:len(dll)], dll) {
		t.Fatal("assembly bytes not stored verbatim")
	}
	in := []Resource{{TypeS3SA, 0, FNV64("Sims3MP"), s3sa}, {TypeXML, 0, FNV64("Sims3MP.Main"), []byte("<base/>")}}
	out, err := Read(Write(in))
	if err != nil {
		t.Fatal(err)
	}
	if len(out) != 2 || out[1].Instance != in[1].Instance || !bytes.Equal(out[0].Data, s3sa) || string(out[1].Data) != "<base/>" {
		t.Fatal("round trip changed the package")
	}
}
